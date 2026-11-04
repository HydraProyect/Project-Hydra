using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CaeManager.Application.Cumplimiento;
using CaeManager.Infrastructure.AsistenteIa;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Issue #2006, incremento A. El conmutador de IA del Tenant propietario
/// (<see cref="IInstruccionTratamientoIaService.EstaHabilitadaAsync"/>) se comprueba en el
/// LLAMADOR de cada cliente del proveedor de IA, no dentro de los clientes <c>Anthropic*</c>:
/// ninguno de ellos conoce al Tenant. Eso hace que la garantía dependa de que cada ruta nueva
/// se acuerde de comprobarlo, y un olvido enviaría contenido del Tenant propietario a un
/// subencargado que su instrucción de tratamiento no autoriza. Estas guardas lo impiden en
/// origen, con una lista cerrada:
/// <list type="number">
/// <item>Solo las clases conocidas pueden llamar a <c>api.anthropic.com</c>.</item>
/// <item>Toda clase que dependa de una interfaz Application implementada por un cliente de IA
/// (o por la envoltura que lo alcanza) está en una tabla explícita.</item>
/// <item>Cada consumidor de esa tabla inyecta <see cref="IInstruccionTratamientoIaService"/> y
/// usa de verdad el resultado de <c>EstaHabilitadaAsync</c>.</item>
/// </list>
/// No demuestra el ORDEN (que la comprobación preceda a la llamada al proveedor) ni que cada
/// rama del consumidor la respete: eso lo cubren los tests de comportamiento de cada ruta.
/// </summary>
public class TodaRutaDeIaCompruebaElConmutadorDelTenantTests
{
    // Clientes que hablan con el proveedor: lista cerrada. Cada uno implementa una interfaz de
    // Application; de ahí se derivan por reflexión las interfaces cuyos consumidores se auditan.
    private static readonly Type[] ClientesDelProveedor =
    [
        typeof(AnthropicAsistenteIaService),
        typeof(AnthropicDecisionesCerradasService),
        typeof(AnthropicDeteccionGestionCorreoService),
        typeof(AnthropicDeteccionIntencionCaeService),
        typeof(AnthropicDeteccionRelevanciaCaeService),
        typeof(AnthropicDeteccionVisitaCorreoService),
        typeof(AnthropicDocumentAIProvider),
        typeof(AnthropicExtraccionTrabajadoresIaService),
        typeof(AnthropicRedaccionRespuestaIaService),
        typeof(AnthropicSegundaOpinionCriticaIa),
    ];

    // Clases que alcanzan a un cliente por composición sin llamar a la red ellas mismas. No se
    // les exige el conmutador (no conocen al Tenant): se les exige a quienes las consumen.
    private static readonly Dictionary<string, string> Envolturas = new()
    {
        ["DocumentAIRouterService"] = "Elige y reintenta proveedores documentales; no conoce al Tenant. Lo consumen las rutas documentales.",
        ["DocumentAIProviderFactory"] = "Fabrica el IDocumentAIProvider del router; no llama a la red.",
        ["RouterExtraccionMetadatosDocumentoIaService"] = "Adapta IExtraccionMetadatosDocumentoIaService sobre el router documental; su consumidor es la verificación IA.",
        ["ExtraccionTrabajadoresConParserLocalService"] = "Decorador: parser local primero y solo cae a IExtraccionTrabajadoresIaService como respaldo.",
    };

    // Implementaciones de las interfaces de IA que no hablan con el proveedor (dobles de prueba).
    private static readonly Dictionary<string, string> DoblesSinRed = new()
    {
        ["ProveedorFalsoDocumentAI"] = "Doble del arnés E2E de IDocumentAIProvider; no hay red.",
        ["AsistenteDePruebas"] = "Doble sin red de IAsistenteIaService (AsistenteDePruebasExtensions).",
    };

    // Consumidores: clase -> motivo de que su ruta compruebe el conmutador antes de llamar a la IA.
    private static readonly Dictionary<string, string> Consumidores = new()
    {
        ["PreguntarAlAsistenteQueryHandler"] = "Chat «Pregúntale a Hydra»: envía el mensaje y el contexto del Tenant propietario a IAsistenteIaService.",
        ["ProponerPlanAsistenteQueryHandler"] = "Agente de órdenes de Vega: clasifica el mensaje con IDecisionesCerradasAsistenteService; su comprobación cubre también la siguiente llamada al asistente.",
        ["GenerarRespuestaSugeridaCommandHandler"] = "Respuesta sugerida: envía el hilo de la conversación a IRedaccionRespuestaIa.",
        ["SugerenciaGestionCorreoService"] = "Detección de gestiones en correo: envía el cuerpo del correo a IDeteccionGestionCorreoService.",
        ["SugerenciaVisitaCorreoService"] = "Detección de visitas en correo: envía el cuerpo del correo a IDeteccionVisitaCorreoService.",
        ["IntencionConversacionService"] = "Intención de una conversación CAE: envía mensajes a IDeteccionIntencionCaeService.",
        ["RelevanciaCaeService"] = "Relevancia CAE de un correo: envía el correo a IDeteccionRelevanciaCaeService.",
        ["AlertaCriticaService"] = "Segunda opinión de alertas críticas: envía el texto del mensaje a ISegundaOpinionCriticaIa.",
        ["DeteccionTrabajadoresService"] = "Extracción de trabajadores desde adjuntos: envía el documento a IExtraccionTrabajadoresIaService.",
        ["VerificacionIaDocumentoService"] = "Verificación IA de un documento: extracción de metadatos vía IExtraccionMetadatosDocumentoIaService (router documental).",
        ["DetectarCamposDocumentoQueryHandler"] = "Detección de campos de un documento: envía el PDF al router documental IDocumentAIRouterService.",
        ["DetectarCamposPlantillaQueryHandler"] = "Detección de campos de una plantilla: envía el PDF al router documental IDocumentAIRouterService.",
        ["DetectarActualizacionDocumentoDesdeAdjuntoQueryHandler"] = "Detección de actualización de documento desde un adjunto: envía el adjunto al router documental IDocumentAIRouterService.",
    };
    [Fact]
    public void Solo_las_clases_conocidas_llaman_a_api_anthropic_com()
    {
        var conocidas = ClientesDelProveedor.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var encontradas = new HashSet<string>(StringComparer.Ordinal);

        foreach (var fichero in FicherosFuente.Value.Where(f => f.Texto.Contains("anthropic.com", StringComparison.OrdinalIgnoreCase)))
        {
            var raiz = CSharpSyntaxTree.ParseText(fichero.Texto).GetRoot();

            foreach (var ficha in raiz.DescendantTokens().Where(EsTextoDeCadena))
            {
                if (!ficha.ValueText.Contains("anthropic.com", StringComparison.OrdinalIgnoreCase))
                    continue;

                var tipo = ficha.Parent?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                encontradas.Add(tipo?.Identifier.Text ?? $"(fuera de un tipo, {fichero.RutaRelativa})");
            }
        }

        var nuevas = encontradas.Except(conocidas).Order().ToList();
        nuevas.Should().BeEmpty("{0}",
            "Estas clases llaman a anthropic.com sin estar en la lista cerrada de clientes del proveedor: " +
            string.Join(", ", nuevas) + ". Un cliente nuevo del proveedor de IA tiene que (1) implementar una " +
            "interfaz de Application, (2) añadirse a ClientesDelProveedor en este test y (3) tener consumidores que " +
            "comprueben IInstruccionTratamientoIaService.EstaHabilitadaAsync antes de usarlo (issue #2006).");

        var obsoletas = conocidas.Except(encontradas).Order().ToList();
        obsoletas.Should().BeEmpty("{0}",
            "Estas clases de ClientesDelProveedor ya no contienen ninguna llamada literal a anthropic.com: " +
            string.Join(", ", obsoletas) + ". Si ya no llaman al proveedor, quítalas de la lista; si lo hacen por otra vía " +
            "(constante, configuración), esta guarda no las ve y hay que revisarla.");
    }

    [Fact]
    public void Toda_clase_que_implementa_una_interfaz_de_IA_es_un_cliente_conocido_una_envoltura_o_un_doble()
    {
        var conocidas = ClientesDelProveedor.Select(t => t.Name)
            .Concat(Envolturas.Keys).Concat(DoblesSinRed.Keys).ToHashSet(StringComparer.Ordinal);

        var desconocidas = TodosLosTipos.Value
            .Where(t => t is { IsClass: true, IsAbstract: false } && !EsGenerado(t))
            .Where(t => InterfacesDeIa.Value.Any(i => i.IsAssignableFrom(t)))
            .Select(NombreCorto)
            .Where(n => !conocidas.Contains(n))
            .Order().ToList();

        desconocidas.Should().BeEmpty("{0}",
            "Estas clases implementan una interfaz de IA sin estar clasificadas: " + string.Join(", ", desconocidas) +
            ". Si llaman a un proveedor, deben añadirse a ClientesDelProveedor (y a su guarda de red); si solo " +
            "delegan en otro servicio de IA, a Envolturas; si son un doble sin red, a DoblesSinRed (issue #2006).");

        var fantasmas = Envolturas.Keys.Concat(DoblesSinRed.Keys)
            .Where(n => !TodosLosTipos.Value.Any(t => NombreCorto(t) == n)).Order().ToList();
        fantasmas.Should().BeEmpty("{0}",
            "Estas entradas de Envolturas o DoblesSinRed ya no existen como tipo: " + string.Join(", ", fantasmas) +
            ". Quítalas de la tabla.");
    }

    [Fact]
    public void Todo_consumidor_de_una_interfaz_de_IA_esta_en_la_tabla_y_ninguna_entrada_esta_obsoleta()
    {
        var clasificadas = ClientesDelProveedor.Select(t => t.Name)
            .Concat(DoblesSinRed.Keys).ToHashSet(StringComparer.Ordinal);

        var descubiertos = DescubrirConsumidores();

        var nuevos = descubiertos.Keys.Where(n => !Consumidores.ContainsKey(n) && !Envolturas.ContainsKey(n)
            && !clasificadas.Contains(n)).Order().ToList();
        nuevos.Should().BeEmpty("{0}",
            "Estas clases dependen de un servicio de IA (" + string.Join(", ", nuevos.Select(n => $"{n} <- {string.Join("+", descubiertos[n])}")) +
            ") y no están en la tabla de Consumidores de este test. Una ruta nueva que llama a la IA tiene que comprobar " +
            "IInstruccionTratamientoIaService.EstaHabilitadaAsync del Tenant propietario ANTES de usar el servicio, y después " +
            "añadirse a Consumidores con el motivo (issue #2006). Si solo envuelve a otro servicio de IA sin llamar al proveedor, va en Envolturas.");

        var obsoletos = Consumidores.Keys.Where(n => !descubiertos.ContainsKey(n)).Order().ToList();
        obsoletos.Should().BeEmpty("{0}",
            "Estas entradas de Consumidores ya no dependen de ningún servicio de IA: " + string.Join(", ", obsoletos) +
            ". Quítalas de la tabla (o comprueba que el consumidor no haya pasado a alcanzar la IA por otra vía).");
    }

    [Fact]
    public void Todo_consumidor_de_la_tabla_inyecta_el_conmutador_y_usa_su_resultado()
    {
        var problemas = new List<string>();

        foreach (var nombre in Consumidores.Keys.Order())
        {
            var tipos = TodosLosTipos.Value.Where(t => NombreCorto(t) == nombre).ToList();
            if (tipos.Count == 0)
            {
                problemas.Add($"{nombre}: no existe como tipo.");
                continue;
            }

            var variables = tipos.SelectMany(NombresDeMiembrosDelConmutador).ToHashSet(StringComparer.Ordinal);
            if (variables.Count == 0)
            {
                problemas.Add($"{nombre}: ya no recibe {nameof(IInstruccionTratamientoIaService)} (ni en constructor, ni en campo, ni en propiedad).");
                continue;
            }

            var usos = LlamadasAEstaHabilitada(nombre, variables).ToList();
            if (usos.Count == 0)
                problemas.Add($"{nombre}: recibe el conmutador pero ninguna llamada a EstaHabilitadaAsync sobre ({string.Join(", ", variables)}).");
            else if (usos.All(u => u.Descartada))
                problemas.Add($"{nombre}: llama a EstaHabilitadaAsync pero descarta el resultado, así que no condiciona nada.");
        }

        problemas.Should().BeEmpty("{0}",
            "Estas rutas llaman a un servicio de IA sin comprobar el conmutador del Tenant propietario (issue #2006):\n  " +
            string.Join("\n  ", problemas) +
            "\nInyecta IInstruccionTratamientoIaService y usa `if (... !await instruccionTratamientoIa.EstaHabilitadaAsync(tenantId, ct)) { ... }` " +
            "antes de llamar al servicio de IA. El tenantId es el del Tenant propietario de los datos, no el de quien opera.");
    }

    // ---------------------------------------------------------------- descubrimiento

    private static readonly Lazy<Type[]> TodosLosTipos = new(() =>
        new[]
        {
            typeof(IInstruccionTratamientoIaService).Assembly,
            typeof(AnthropicDocumentAIProvider).Assembly,
            typeof(CaeManager.Web.Features.AsistenteIa.DisponibilidadAsistente).Assembly,
        }.SelectMany(a => a.GetTypes()).ToArray());

    /// <summary>Interfaces de Application que implementan los clientes y las envolturas conocidas.</summary>
    private static readonly Lazy<HashSet<Type>> InterfacesDeIa = new(() =>
    {
        var aplicacion = typeof(IInstruccionTratamientoIaService).Assembly;

        return ClientesDelProveedor
            .Concat(TodosLosTipos.Value.Where(t => Envolturas.ContainsKey(NombreCorto(t))))
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.Assembly == aplicacion && i.IsPublic)
            .ToHashSet();
    });

    private static Dictionary<string, List<string>> DescubrirConsumidores()
    {
        var resultado = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        const BindingFlags todos = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        foreach (var tipo in TodosLosTipos.Value.Where(t => t.IsClass && !EsGenerado(t) && !EsParteDeUnaEnvoltura(t)))
        {
            var tipos = new List<Type>();
            tipos.AddRange(tipo.GetConstructors(todos).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType));
            tipos.AddRange(tipo.GetFields(todos).Select(f => f.FieldType));
            tipos.AddRange(tipo.GetProperties(todos).Select(p => p.PropertyType));

            // Una clase estática solo puede alcanzar la IA por parámetros de método (extensiones).
            if (tipo.IsAbstract && tipo.IsSealed)
                tipos.AddRange(tipo.GetMethods(todos).SelectMany(m => m.GetParameters()).Select(p => p.ParameterType));

            var alcanzadas = tipos.SelectMany(Desenvolver).Where(InterfacesDeIa.Value.Contains)
                .Select(i => i.Name).Distinct().Order().ToList();

            if (alcanzadas.Count > 0)
                resultado[NombreCorto(tipo)] = alcanzadas;
        }

        return resultado;
    }

    private static IEnumerable<Type> Desenvolver(Type tipo)
    {
        yield return tipo;

        if (tipo.HasElementType && tipo.GetElementType() is { } elemento)
            foreach (var t in Desenvolver(elemento)) yield return t;

        if (tipo.IsGenericType)
            foreach (var argumento in tipo.GetGenericArguments())
                foreach (var t in Desenvolver(argumento)) yield return t;
    }

    private static IEnumerable<string> NombresDeMiembrosDelConmutador(Type tipo)
    {
        const BindingFlags todos = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var conmutador = typeof(IInstruccionTratamientoIaService);

        return tipo.GetConstructors(todos).SelectMany(c => c.GetParameters())
            .Where(p => p.ParameterType == conmutador).Select(p => p.Name!)
            .Concat(tipo.GetFields(todos).Where(f => f.FieldType == conmutador).Select(f => f.Name))
            .Concat(tipo.GetProperties(todos).Where(p => p.PropertyType == conmutador).Select(p => p.Name));
    }

    private static bool EsGenerado(Type tipo) =>
        tipo.Name.StartsWith('<') || tipo.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);

    // Un tipo anidado de una envoltura (p. ej. LecturaEscaneadaPorPaginas dentro del router documental)
    // es parte de ella, no un consumidor distinto.
    private static bool EsParteDeUnaEnvoltura(Type tipo)
    {
        for (var padre = tipo.DeclaringType; padre is not null; padre = padre.DeclaringType)
            if (Envolturas.ContainsKey(NombreCorto(padre)))
                return true;

        return false;
    }

    private static string NombreCorto(Type tipo) => tipo.Name;

    // ---------------------------------------------------------------- lectura del código fuente

    private sealed record FicheroFuente(string RutaRelativa, string Texto);

    private static readonly Lazy<List<FicheroFuente>> FicherosFuente = new(() =>
    {
        var raiz = RaizDelRepositorio();
        var separador = Path.DirectorySeparatorChar;

        return Directory.EnumerateFiles(Path.Combine(raiz, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separador}obj{separador}") && !f.Contains($"{separador}bin{separador}"))
            .Select(f => new FicheroFuente(Path.GetRelativePath(raiz, f), File.ReadAllText(f)))
            .ToList();
    });

    private static bool EsTextoDeCadena(SyntaxToken ficha) =>
        ficha.IsKind(SyntaxKind.StringLiteralToken)
        || ficha.IsKind(SyntaxKind.InterpolatedStringTextToken)
        || ficha.IsKind(SyntaxKind.Utf8StringLiteralToken)
        || ficha.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
        || ficha.IsKind(SyntaxKind.MultiLineRawStringLiteralToken);

    private sealed record UsoDelConmutador(string Fichero, bool Descartada);

    /// <summary>
    /// Llamadas <c>x.EstaHabilitadaAsync(...)</c> dentro de la declaración de la clase cuyo receptor
    /// es uno de los miembros que reciben el conmutador. Una llamada cuyo resultado se descarta
    /// (<c>await x.EstaHabilitadaAsync(...);</c> como sentencia) no condiciona nada.
    /// </summary>
    private static IEnumerable<UsoDelConmutador> LlamadasAEstaHabilitada(string tipo, ISet<string> miembros)
    {
        var declaracion = new Regex($@"\b(class|record|struct)\s+{Regex.Escape(tipo)}\b");

        foreach (var fichero in FicherosFuente.Value.Where(f => f.Texto.Contains("EstaHabilitadaAsync", StringComparison.Ordinal) && declaracion.IsMatch(f.Texto)))
        {
            var raiz = CSharpSyntaxTree.ParseText(fichero.Texto).GetRoot();

            foreach (var clase in raiz.DescendantNodes().OfType<TypeDeclarationSyntax>().Where(t => t.Identifier.Text == tipo))
            {
                foreach (var llamada in clase.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (llamada.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "EstaHabilitadaAsync" } acceso)
                        continue;

                    var receptor = acceso.Expression switch
                    {
                        IdentifierNameSyntax id => id.Identifier.Text,
                        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: var nombre } => nombre.Identifier.Text,
                        _ => null,
                    };

                    if (receptor is null || !miembros.Contains(receptor))
                        continue;

                    SyntaxNode actual = llamada;
                    if (actual.Parent is AwaitExpressionSyntax espera)
                        actual = espera;

                    yield return new UsoDelConmutador(fichero.RutaRelativa, actual.Parent is ExpressionStatementSyntax);
                }
            }
        }
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
            " — este test necesita el árbol fuente del repositorio.");
    }
}
