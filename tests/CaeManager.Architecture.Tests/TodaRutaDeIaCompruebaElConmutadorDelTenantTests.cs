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
/// subencargado que su instrucción de tratamiento no autoriza.
///
/// Complementa a <see cref="ConsumidoresDeIaConsultanLaInstruccionDeTratamientoTests"/> (ratchet
/// de texto sobre constructores de Application, con lista propia de puertos): este deriva los
/// puertos de los clientes reales, mira también Infrastructure y Web, ve más formas de consumo y
/// exige que el resultado de <c>EstaHabilitadaAsync</c> se use. La lista de clientes es la única de
/// <see cref="ClientesDeIaDelProveedor"/>, compartida con el ratchet de resiliencia HTTP.
///
/// Qué garantiza, con lista cerrada:
/// <list type="number">
/// <item>Solo los clientes conocidos contienen un literal de <c>anthropic.com</c>.</item>
/// <item>Toda implementación de una interfaz de IA está clasificada (cliente, envoltura o doble).</item>
/// <item>Todo consumidor detectado está en una tabla explícita con su motivo. Se detecta: parámetro
/// de constructor, campo o propiedad (reflexión, incluidas las propiedades <c>[Inject]</c> de los
/// componentes Blazor), parámetro de cualquier método, lambda o <c>[FromServices]</c> (sintaxis),
/// <c>GetService/GetRequiredService/GetServices/CreateInstance&lt;T&gt;</c> o con <c>typeof</c>
/// (sintaxis), tanto contra la interfaz de IA como contra el cliente concreto.</item>
/// <item>Cada consumidor de la tabla recibe o resuelve <see cref="IInstruccionTratamientoIaService"/>
/// y llama a <c>EstaHabilitadaAsync</c> sobre él sin descartar el resultado (ni sentencia suelta,
/// ni <c>.ConfigureAwait(false)</c> suelto, ni variable que nunca se lee).</item>
/// </list>
///
/// Qué NO garantiza (huecos declarados): que la comprobación preceda a la llamada al proveedor ni
/// que el resultado corte de verdad la ruta; que el <c>tenantId</c> sea el del Tenant propietario;
/// consumos por reflexión (<c>Activator</c>, <c>Type.GetType</c>), por <c>dynamic</c> o por un tipo
/// intermedio nuevo que no sea una interfaz derivada de un cliente o una envoltura de la tabla; y
/// ficheros <c>.razor</c>/<c>.cshtml</c> leídos como sintaxis (las propiedades <c>[Inject]</c>
/// sí las ve la reflexión sobre el ensamblado). Eso lo cubren los tests de comportamiento de cada ruta.
/// </summary>
public class TodaRutaDeIaCompruebaElConmutadorDelTenantTests
{
    private const string Conmutador = nameof(IInstruccionTratamientoIaService);

    private static readonly HashSet<string> MetodosDeResolucion = new(StringComparer.Ordinal)
    {
        "GetService", "GetRequiredService", "GetServices",
        "GetKeyedService", "GetRequiredKeyedService", "GetKeyedServices",
        "CreateInstance", "GetServiceOrCreateInstance",
    };

    // Clases que alcanzan a un cliente por composición sin llamar a la red ellas mismas. No se
    // les exige el conmutador (no conocen al Tenant): se les exige a quienes las consumen.
    // Sus tipos anidados cuentan como parte de ellas.
    private static readonly Dictionary<string, string> Envolturas = new()
    {
        ["CaeManager.Application.DocumentosIa.DocumentAIRouterService"] = "Elige y reintenta proveedores documentales; no conoce al Tenant. Lo consumen las rutas documentales.",
        ["CaeManager.Application.DocumentosIa.Common.DocumentAIProviderFactory"] = "Fabrica el IDocumentAIProvider del router; no llama a la red.",
        ["CaeManager.Application.DocumentosIa.RouterExtraccionMetadatosDocumentoIaService"] = "Adapta IExtraccionMetadatosDocumentoIaService sobre el router documental; su consumidor es la verificación IA.",
        ["CaeManager.Application.Trabajadores.Extraccion.ExtraccionTrabajadoresConParserLocalService"] = "Decorador: parser local primero y solo cae a IExtraccionTrabajadoresIaService como respaldo.",
    };

    // Implementaciones de las interfaces de IA que no hablan con el proveedor (dobles de prueba).
    private static readonly Dictionary<string, string> DoblesSinRed = new()
    {
        ["CaeManager.Infrastructure.AsistenteIa.ProveedorFalsoDocumentAI"] = "Doble del arnés E2E de IDocumentAIProvider; no hay red.",
        ["CaeManager.Infrastructure.DependencyInjection.AsistenteDePruebasExtensions.AsistenteDePruebas"] = "Doble sin red de IAsistenteIaService (AsistenteDePruebasExtensions).",
    };

    // Registro de servicios: construye o resuelve los servicios de IA para el contenedor, no los usa.
    private static readonly Dictionary<string, string> RegistrosDeDi = new()
    {
        ["CaeManager.Infrastructure.DependencyInjection.InfrastructureServiceCollectionExtensions"] = "Registra los clientes concretos (AnthropicDocumentAIProvider, AnthropicExtraccionTrabajadoresIaService) y los expone por su interfaz; no los usa.",
    };
    // Consumidores: clase -> motivo de que su ruta compruebe el conmutador antes de llamar a la IA.
    private static readonly Dictionary<string, string> Consumidores = new()
    {
        ["CaeManager.Application.AsistenteIa.Queries.PreguntarAlAsistente.PreguntarAlAsistenteQueryHandler"] = "Chat «Pregúntale a Hydra»: envía el mensaje y el contexto del Tenant propietario a IAsistenteIaService.",
        ["CaeManager.Application.AsistenteIa.Queries.ProponerPlan.ProponerPlanAsistenteQueryHandler"] = "Agente de órdenes de Vega: clasifica el mensaje con IDecisionesCerradasAsistenteService; su comprobación cubre también la siguiente llamada al asistente.",
        ["CaeManager.Application.Comunicaciones.Commands.GenerarRespuestaSugerida.GenerarRespuestaSugeridaCommandHandler"] = "Respuesta sugerida: envía el hilo de la conversación a IRedaccionRespuestaIa.",
        ["CaeManager.Application.Comunicaciones.Deteccion.SugerenciaGestionCorreoService"] = "Detección de gestiones en correo: envía el cuerpo del correo a IDeteccionGestionCorreoService.",
        ["CaeManager.Application.Comunicaciones.Deteccion.SugerenciaVisitaCorreoService"] = "Detección de visitas en correo: envía el cuerpo del correo a IDeteccionVisitaCorreoService.",
        ["CaeManager.Application.Comunicaciones.Deteccion.IntencionConversacionService"] = "Intención de una conversación CAE: envía mensajes a IDeteccionIntencionCaeService.",
        ["CaeManager.Application.Comunicaciones.Deteccion.RelevanciaCaeService"] = "Relevancia CAE de un correo: envía el correo a IDeteccionRelevanciaCaeService.",
        ["CaeManager.Application.Comunicaciones.Deteccion.AlertaCriticaService"] = "Segunda opinión de alertas críticas: envía el texto del mensaje a ISegundaOpinionCriticaIa.",
        ["CaeManager.Application.Trabajadores.Deteccion.DeteccionTrabajadoresService"] = "Extracción de trabajadores desde adjuntos: envía el documento a IExtraccionTrabajadoresIaService.",
        ["CaeManager.Application.Documentos.Verificacion.VerificacionIaDocumentoService"] = "Verificación IA de un documento: extracción de metadatos vía IExtraccionMetadatosDocumentoIaService (router documental).",
        ["CaeManager.Application.Documentos.Queries.DetectarCamposDocumento.DetectarCamposDocumentoQueryHandler"] = "Detección de campos de un documento: envía el PDF al router documental IDocumentAIRouterService.",
        ["CaeManager.Application.Plantillas.Queries.DetectarCamposPlantilla.DetectarCamposPlantillaQueryHandler"] = "Detección de campos de una plantilla: envía el PDF al router documental IDocumentAIRouterService.",
        ["CaeManager.Application.Comunicaciones.Queries.DetectarActualizacionDocumentoDesdeAdjunto.DetectarActualizacionDocumentoDesdeAdjuntoQueryHandler"] = "Detección de actualización de documento desde un adjunto: envía el adjunto al router documental IDocumentAIRouterService.",
    };
    // ---------------------------------------------------------------- tests sobre el código real

    [Fact]
    public void Solo_las_clases_conocidas_llaman_a_api_anthropic_com()
    {
        var conocidas = ClientesDeIaDelProveedor.Tipos.Select(NombreCompleto).ToHashSet(StringComparer.Ordinal);
        var encontradas = new HashSet<string>(StringComparer.Ordinal);

        foreach (var fichero in FicherosFuente.Value.Where(f => f.Texto.Contains("anthropic.com", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var ficha in fichero.Raiz.Value.DescendantTokens().Where(EsTextoDeCadena))
            {
                if (!ficha.ValueText.Contains("anthropic.com", StringComparison.OrdinalIgnoreCase))
                    continue;

                var tipo = ficha.Parent?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                encontradas.Add(tipo is null ? $"(fuera de un tipo, {fichero.RutaRelativa})" : NombreCompleto(tipo));
            }
        }

        var nuevas = encontradas.Except(conocidas).Order().ToList();
        nuevas.Should().BeEmpty("{0}",
            "Estas clases llaman a anthropic.com sin estar en la lista cerrada de clientes del proveedor: " +
            string.Join(", ", nuevas) + ". Un cliente nuevo del proveedor de IA tiene que (1) implementar una " +
            "interfaz de Application, (2) añadirse a ClientesDeIaDelProveedor y (3) tener consumidores que " +
            "comprueben IInstruccionTratamientoIaService.EstaHabilitadaAsync antes de usarlo (issue #2006).");

        var obsoletas = conocidas.Except(encontradas).Order().ToList();
        obsoletas.Should().BeEmpty("{0}",
            "Estas clases de ClientesDeIaDelProveedor ya no contienen ninguna llamada literal a anthropic.com: " +
            string.Join(", ", obsoletas) + ". Si ya no llaman al proveedor, quítalas de la lista; si lo hacen por otra vía " +
            "(constante, configuración), esta guarda no las ve y hay que revisarla.");
    }

    [Fact]
    public void Toda_clase_que_implementa_una_interfaz_de_IA_es_un_cliente_conocido_una_envoltura_o_un_doble()
    {
        var conocidas = ClientesDeIaDelProveedor.Tipos.Select(NombreCompleto)
            .Concat(Envolturas.Keys).Concat(DoblesSinRed.Keys).ToHashSet(StringComparer.Ordinal);

        var desconocidas = TodosLosTipos.Value
            .Where(t => t is { IsClass: true, IsAbstract: false } && !EsGenerado(t))
            .Where(t => InterfacesDeIa.Value.Any(i => i.IsAssignableFrom(t)))
            .Select(NombreCompleto)
            .Where(n => !conocidas.Contains(n))
            .Order().ToList();

        desconocidas.Should().BeEmpty("{0}",
            "Estas clases implementan una interfaz de IA sin estar clasificadas: " + string.Join(", ", desconocidas) +
            ". Si llaman a un proveedor, deben añadirse a ClientesDeIaDelProveedor; si solo delegan en otro servicio de IA, " +
            "a Envolturas; si son un doble sin red, a DoblesSinRed (issue #2006).");

        var fantasmas = Envolturas.Keys.Concat(DoblesSinRed.Keys)
            .Where(n => !TodosLosTipos.Value.Any(t => NombreCompleto(t) == n)).Order().ToList();
        fantasmas.Should().BeEmpty("{0}",
            "Estas entradas de Envolturas o DoblesSinRed ya no existen como tipo: " + string.Join(", ", fantasmas) +
            ". Quítalas de la tabla.");
    }

    [Fact]
    public void Todo_consumidor_de_un_servicio_de_IA_esta_en_la_tabla_y_ninguna_entrada_esta_obsoleta()
    {
        var descubiertos = DescubrirConsumidores();

        var nuevos = descubiertos.Keys.Where(n => !EstaClasificado(n)).Order().ToList();
        nuevos.Should().BeEmpty("{0}",
            "Estas clases dependen de un servicio de IA o de un cliente del proveedor (" +
            string.Join("; ", nuevos.Select(n => $"{n} <- {string.Join(" + ", descubiertos[n])}")) +
            ") y no están en la tabla de Consumidores de este test. Una ruta nueva que llama a la IA tiene que comprobar " +
            "IInstruccionTratamientoIaService.EstaHabilitadaAsync del Tenant propietario ANTES de usar el servicio, y después " +
            "añadirse a Consumidores con el motivo (issue #2006). Si solo envuelve a otro servicio de IA sin llamar al proveedor, va en Envolturas; " +
            "si es el registro de servicios, en RegistrosDeDi.");

        var obsoletos = Consumidores.Keys.Concat(RegistrosDeDi.Keys).Where(n => !descubiertos.ContainsKey(n)).Order().ToList();
        obsoletos.Should().BeEmpty("{0}",
            "Estas entradas de Consumidores o RegistrosDeDi ya no dependen de ningún servicio de IA: " + string.Join(", ", obsoletos) +
            ". Quítalas de la tabla (o comprueba que el consumidor no haya pasado a alcanzar la IA por otra vía).");
    }

    [Fact]
    public void Todo_consumidor_de_la_tabla_recibe_el_conmutador_y_no_descarta_su_resultado()
    {
        var problemas = new List<string>();

        foreach (var nombre in Consumidores.Keys.Order())
        {
            var clases = DeclaracionesDe(nombre).ToList();
            if (clases.Count == 0)
            {
                problemas.Add($"{nombre}: no se encuentra su declaración en el código fuente.");
                continue;
            }

            var receptores = ReceptoresDelConmutador(clases);
            if (receptores.Count == 0)
            {
                problemas.Add($"{nombre}: ya no recibe ni resuelve {Conmutador}.");
                continue;
            }

            var usos = UsosDeEstaHabilitada(clases, receptores).ToList();
            if (usos.Count == 0)
                problemas.Add($"{nombre}: recibe el conmutador pero no hay ninguna llamada a EstaHabilitadaAsync sobre ({string.Join(", ", receptores)}).");
            else if (usos.All(u => u.Descartada is not null))
                problemas.Add($"{nombre}: llama a EstaHabilitadaAsync pero {usos[0].Descartada}, así que no condiciona nada.");
        }

        problemas.Should().BeEmpty("{0}",
            "Estas rutas llaman a un servicio de IA sin comprobar el conmutador del Tenant propietario (issue #2006):\n  " +
            string.Join("\n  ", problemas) +
            "\nInyecta IInstruccionTratamientoIaService y usa `if (... !await instruccionTratamientoIa.EstaHabilitadaAsync(tenantId, ct)) { ... }` " +
            "antes de llamar al servicio de IA. El tenantId es el del Tenant propietario de los datos, no el de quien opera.");
    }

    [Fact]
    public void Los_puertos_derivados_de_los_clientes_son_los_del_ratchet_de_Nivel_0()
    {
        var derivados = InterfacesDeIa.Value.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
        var delRatchet = ConsumidoresDeIaConsultanLaInstruccionDeTratamientoTests.PuertosDeIa.ToHashSet(StringComparer.Ordinal);

        derivados.Except(delRatchet).Should().BeEmpty(
            "ConsumidoresDeIaConsultanLaInstruccionDeTratamientoTests.PuertosDeIa no incluye estas interfaces de IA derivadas " +
            "de los clientes: sus consumidores escapan a ese ratchet. Añádelas a su lista");
        delRatchet.Except(derivados).Should().BeEmpty(
            "PuertosDeIa del ratchet de Nivel 0 contiene interfaces que ya no son de ningún cliente ni envoltura conocida");
    }

    // ---------------------------------------------------------------- sensibilidad del instrumento

    [Theory]
    [InlineData("void M(IServiceProvider sp) { var x = sp.GetRequiredService<IAsistenteIaService>(); }", true)]
    [InlineData("void M(IServiceProvider sp) { var x = sp.GetServices<IAsistenteIaService>(); }", true)]
    [InlineData("void M(IServiceProvider sp) { var x = sp.GetService(typeof(AnthropicDocumentAIProvider)); }", true)]
    [InlineData("void M(IServiceProvider sp) { var x = ActivatorUtilities.CreateInstance<AnthropicDocumentAIProvider>(sp); }", true)]
    [InlineData("void Mapear(IEndpointRouteBuilder app) { app.MapPost(\"/x\", (IAsistenteIaService ia) => 1); }", true)]
    [InlineData("void Mapear(IEndpointRouteBuilder app) { app.MapGet(\"/x\", async ([FromServices] AnthropicDocumentAIProvider p) => 1); }", true)]
    [InlineData("void M([FromServices] IAsistenteIaService ia) { }", true)]
    [InlineData("void M(IEnumerable<IAsistenteIaService> todos) { }", true)]
    [InlineData("void M(IServiceCollection s) { s.AddScoped<IAsistenteIaService, OtraCosa>(); }", false)]
    [InlineData("void M(IServiceProvider sp) { var x = sp.GetRequiredService<IOtraCosa>(); }", false)]
    public void El_detector_sintactico_ve_las_formas_de_consumo_y_no_el_registro(string miembro, bool esperado)
    {
        var raiz = CSharpSyntaxTree.ParseText($"namespace N; public class C {{ {miembro} }}").GetRoot();
        var alcanzables = new HashSet<string> { "IAsistenteIaService", "AnthropicDocumentAIProvider" };

        var consumos = ConsumosPorSintaxis(raiz, alcanzables, "(nivel superior)").ToList();

        (consumos.Count > 0).Should().Be(esperado, "en: " + miembro);
        if (esperado)
            consumos.Select(c => c.Clase).Distinct().Should().Equal("N.C");
    }

    [Fact]
    public void El_detector_sintactico_ignora_las_interfaces_y_atribuye_las_lambdas_de_nivel_superior()
    {
        var alcanzables = new HashSet<string> { "IAsistenteIaService" };

        var interfaz = CSharpSyntaxTree.ParseText("interface I { void M(IAsistenteIaService x); }").GetRoot();
        ConsumosPorSintaxis(interfaz, alcanzables, "(nivel superior)").Should().BeEmpty();

        var superior = CSharpSyntaxTree.ParseText("app.MapGet(\"/x\", (IAsistenteIaService ia) => 1);").GetRoot();
        ConsumosPorSintaxis(superior, alcanzables, "(nivel superior de Program.cs)").Select(c => c.Clase)
            .Should().Equal("(nivel superior de Program.cs)");
    }

    [Theory]
    [InlineData("if (!await x.EstaHabilitadaAsync(t, ct)) return;", false)]
    [InlineData("return await x.EstaHabilitadaAsync(t, ct);", false)]
    [InlineData("if (await x.EstaHabilitadaAsync(t, ct).ConfigureAwait(false)) { }", false)]
    [InlineData("var ok = await x.EstaHabilitadaAsync(t, ct); if (!ok) return;", false)]
    [InlineData("bool ok; ok = await x.EstaHabilitadaAsync(t, ct); if (!ok) return;", false)]
    [InlineData("await x.EstaHabilitadaAsync(t, ct);", true)]
    [InlineData("await x.EstaHabilitadaAsync(t, ct).ConfigureAwait(false);", true)]
    [InlineData("var ok = await x.EstaHabilitadaAsync(t, ct);", true)]
    [InlineData("var ok = await x.EstaHabilitadaAsync(t, ct).ConfigureAwait(false);", true)]
    [InlineData("var ok = await x.EstaHabilitadaAsync(t, ct); ok = false;", true)]
    [InlineData("_ = await x.EstaHabilitadaAsync(t, ct);", true)]
    public void El_clasificador_distingue_el_resultado_usado_del_descartado(string cuerpo, bool descartado)
    {
        var raiz = CSharpSyntaxTree.ParseText($"class C {{ async Task<bool> M() {{ {cuerpo} return true; }} }}").GetRoot();
        var llamada = raiz.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "EstaHabilitadaAsync" });

        (MotivoDeDescarte(llamada) is not null).Should().Be(descartado, "en: " + cuerpo);
    }

    // ---------------------------------------------------------------- descubrimiento por reflexión

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

        return ClientesDeIaDelProveedor.Tipos
            .Concat(TodosLosTipos.Value.Where(t => Envolturas.ContainsKey(NombreCompleto(t))))
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.Assembly == aplicacion && i.IsPublic)
            .ToHashSet();
    });

    /// <summary>Lo que un consumidor no debe poder alcanzar sin pasar por el conmutador: las interfaces y los clientes concretos.</summary>
    private static readonly Lazy<HashSet<Type>> TiposAlcanzables = new(() =>
        InterfacesDeIa.Value.Concat(ClientesDeIaDelProveedor.Tipos).ToHashSet());

    private static readonly Lazy<HashSet<string>> NombresAlcanzables = new(() =>
        TiposAlcanzables.Value.Select(t => t.Name).ToHashSet(StringComparer.Ordinal));

    private sealed record Consumo(string Clase, string Alcanzado, string Via);

    private static Dictionary<string, List<string>> DescubrirConsumidores()
    {
        var consumos = new List<Consumo>();
        const BindingFlags todos = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        foreach (var tipo in TodosLosTipos.Value.Where(t => t.IsClass && !EsGenerado(t)))
        {
            var miembros = new List<(Type Tipo, string Via)>();
            miembros.AddRange(tipo.GetConstructors(todos).SelectMany(c => c.GetParameters()).Select(p => (p.ParameterType, "constructor")));
            miembros.AddRange(tipo.GetFields(todos).Select(f => (f.FieldType, "campo")));
            miembros.AddRange(tipo.GetProperties(todos).Select(p => (p.PropertyType, "propiedad")));

            // Una clase estática solo puede alcanzar la IA por parámetros de método (extensiones).
            if (tipo.IsAbstract && tipo.IsSealed)
                miembros.AddRange(tipo.GetMethods(todos).SelectMany(m => m.GetParameters()).Select(p => (p.ParameterType, "parámetro de método")));

            foreach (var (tipoMiembro, via) in miembros)
                foreach (var alcanzado in Desenvolver(tipoMiembro).Where(TiposAlcanzables.Value.Contains))
                    consumos.Add(new Consumo(NombreCompleto(tipo), alcanzado.Name, via));
        }

        var nombres = NombresAlcanzables.Value;
        foreach (var fichero in FicherosFuente.Value.Where(f => nombres.Any(n => f.Texto.Contains(n, StringComparison.Ordinal))))
            consumos.AddRange(ConsumosPorSintaxis(fichero.Raiz.Value, nombres, $"(nivel superior de {fichero.RutaRelativa})"));

        // Los clientes no se consumen a sí mismos, y las dobles/envolturas/registro se clasifican aparte.
        return consumos
            .GroupBy(c => c.Clase, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => $"{c.Alcanzado} ({c.Via})").Distinct().Order().ToList(), StringComparer.Ordinal);
    }

    private static bool EstaClasificado(string clase) =>
        Consumidores.ContainsKey(clase)
        || RegistrosDeDi.ContainsKey(clase)
        || DoblesSinRed.ContainsKey(clase)
        || ClientesDeIaDelProveedor.Tipos.Any(t => NombreCompleto(t) == clase)
        || Envolturas.Keys.Any(e => clase == e || clase.StartsWith(e + ".", StringComparison.Ordinal));

    private static IEnumerable<Type> Desenvolver(Type tipo)
    {
        yield return tipo;

        if (tipo.HasElementType && tipo.GetElementType() is { } elemento)
            foreach (var t in Desenvolver(elemento)) yield return t;

        if (tipo.IsGenericType)
            foreach (var argumento in tipo.GetGenericArguments())
                foreach (var t in Desenvolver(argumento)) yield return t;
    }

    private static bool EsGenerado(Type tipo) =>
        tipo.Name.StartsWith('<') || tipo.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);

    /// <summary>Nombre completo con espacio de nombres y anidamiento separados por punto, sin la aridad genérica.</summary>
    private static string NombreCompleto(Type tipo) =>
        Regex.Replace((tipo.FullName ?? tipo.Name).Replace('+', '.'), @"`\d+", "");

    // ---------------------------------------------------------------- lectura del código fuente

    private sealed record FicheroFuente(string RutaRelativa, string Texto, Lazy<SyntaxNode> Raiz);

    private static readonly Lazy<List<FicheroFuente>> FicherosFuente = new(() =>
    {
        var raiz = RaizDelRepositorio();
        var separador = Path.DirectorySeparatorChar;

        return Directory.EnumerateFiles(Path.Combine(raiz, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separador}obj{separador}") && !f.Contains($"{separador}bin{separador}"))
            .Select(f =>
            {
                var texto = File.ReadAllText(f);
                return new FicheroFuente(Path.GetRelativePath(raiz, f), texto, new Lazy<SyntaxNode>(() => CSharpSyntaxTree.ParseText(texto).GetRoot()));
            })
            .ToList();
    });

    private static IEnumerable<TypeDeclarationSyntax> DeclaracionesDe(string nombreCompleto)
    {
        var simple = nombreCompleto[(nombreCompleto.LastIndexOf('.') + 1)..];

        return FicherosFuente.Value
            .Where(f => f.Texto.Contains(simple, StringComparison.Ordinal))
            .SelectMany(f => f.Raiz.Value.DescendantNodes().OfType<TypeDeclarationSyntax>())
            .Where(t => !t.IsKind(SyntaxKind.InterfaceDeclaration) && NombreCompleto(t) == nombreCompleto);
    }

    private static string NombreCompleto(TypeDeclarationSyntax tipo)
    {
        var partes = new List<string> { tipo.Identifier.Text };

        for (var nodo = tipo.Parent; nodo is not null; nodo = nodo.Parent)
        {
            if (nodo is TypeDeclarationSyntax padre)
                partes.Insert(0, padre.Identifier.Text);
            else if (nodo is BaseNamespaceDeclarationSyntax espacio)
                partes.Insert(0, espacio.Name.ToString());
        }

        return string.Join('.', partes);
    }

    private static bool EsTextoDeCadena(SyntaxToken ficha) =>
        ficha.IsKind(SyntaxKind.StringLiteralToken)
        || ficha.IsKind(SyntaxKind.InterpolatedStringTextToken)
        || ficha.IsKind(SyntaxKind.Utf8StringLiteralToken)
        || ficha.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
        || ficha.IsKind(SyntaxKind.MultiLineRawStringLiteralToken);

    // ---------------------------------------------------------------- descubrimiento por sintaxis

    private static IEnumerable<string> IdentificadoresDeTipo(TypeSyntax tipo) =>
        tipo.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Select(i => i.Identifier.Text);

    /// <summary>Tipos pedidos al contenedor en una llamada <c>GetService&lt;T&gt;()</c> o <c>GetService(typeof(T))</c>.</summary>
    private static IEnumerable<string> TiposResueltos(InvocationExpressionSyntax llamada)
    {
        var nombre = llamada.Expression switch
        {
            MemberAccessExpressionSyntax acceso => acceso.Name,
            SimpleNameSyntax simple => simple,
            _ => null,
        };

        if (nombre is null || !MetodosDeResolucion.Contains(nombre.Identifier.Text))
            return [];

        var genericos = nombre is GenericNameSyntax g
            ? g.TypeArgumentList.Arguments.SelectMany(IdentificadoresDeTipo)
            : [];
        var deTypeof = llamada.ArgumentList.Arguments
            .Select(a => a.Expression).OfType<TypeOfExpressionSyntax>()
            .SelectMany(t => IdentificadoresDeTipo(t.Type));

        return genericos.Concat(deTypeof);
    }

    /// <summary>
    /// Clases que piden al contenedor un tipo alcanzable (parámetro de cualquier método, lambda o
    /// <c>[FromServices]</c>, o <c>GetService</c> y similares). Un parámetro de interfaz declarada no cuenta;
    /// el código sin clase (lambdas de nivel superior) se atribuye a <paramref name="etiquetaSinTipo"/>.
    /// </summary>
    private static IEnumerable<Consumo> ConsumosPorSintaxis(SyntaxNode raiz, ISet<string> alcanzables, string etiquetaSinTipo)
    {
        foreach (var nodo in raiz.DescendantNodes())
        {
            IEnumerable<string> pedidos;
            string via;

            switch (nodo)
            {
                case ParameterSyntax { Type: { } tipo }:
                    pedidos = IdentificadoresDeTipo(tipo);
                    via = "parámetro de método, lambda o [FromServices]";
                    break;
                case InvocationExpressionSyntax llamada:
                    pedidos = TiposResueltos(llamada);
                    via = "resolución desde el contenedor";
                    break;
                default:
                    continue;
            }

            var alcanzados = pedidos.Where(alcanzables.Contains).Distinct().ToList();
            if (alcanzados.Count == 0)
                continue;

            var contenedor = nodo.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            string clase;
            if (contenedor is null)
            {
                if (nodo.Ancestors().Any(a => a is DelegateDeclarationSyntax))
                    continue;

                clase = etiquetaSinTipo;
            }
            else if (contenedor.IsKind(SyntaxKind.InterfaceDeclaration))
            {
                continue;
            }
            else
            {
                clase = NombreCompleto(contenedor);
            }

            foreach (var alcanzado in alcanzados)
                yield return new Consumo(clase, alcanzado, via);
        }
    }

    // ---------------------------------------------------------------- uso del conmutador

    private sealed record UsoDelConmutador(string? Descartada);

    private const string Resuelto = "<resuelto del contenedor>";

    /// <summary>Nombres con los que la clase recibe o resuelve <see cref="IInstruccionTratamientoIaService"/>.</summary>
    private static HashSet<string> ReceptoresDelConmutador(IReadOnlyCollection<TypeDeclarationSyntax> clases)
    {
        var receptores = new HashSet<string>(StringComparer.Ordinal);

        foreach (var nodo in clases.SelectMany(c => c.DescendantNodes()))
        {
            switch (nodo)
            {
                case ParameterSyntax { Type: { } t } p when IdentificadoresDeTipo(t).Contains(Conmutador):
                    receptores.Add(p.Identifier.Text);
                    break;
                case VariableDeclarationSyntax v when IdentificadoresDeTipo(v.Type).Contains(Conmutador):
                    foreach (var d in v.Variables) receptores.Add(d.Identifier.Text);
                    break;
                case PropertyDeclarationSyntax pr when IdentificadoresDeTipo(pr.Type).Contains(Conmutador):
                    receptores.Add(pr.Identifier.Text);
                    break;
                case InvocationExpressionSyntax i when TiposResueltos(i).Contains(Conmutador):
                    receptores.Add(Resuelto);
                    if (i.Parent?.Parent is VariableDeclaratorSyntax variable)
                        receptores.Add(variable.Identifier.Text);
                    break;
            }
        }

        return receptores;
    }

    private static IEnumerable<UsoDelConmutador> UsosDeEstaHabilitada(IReadOnlyCollection<TypeDeclarationSyntax> clases, ISet<string> receptores)
    {
        foreach (var llamada in clases.SelectMany(c => c.DescendantNodes()).OfType<InvocationExpressionSyntax>())
        {
            if (llamada.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "EstaHabilitadaAsync" } acceso)
                continue;

            var receptor = acceso.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: var nombre } => nombre.Identifier.Text,
                InvocationExpressionSyntax resolucion when TiposResueltos(resolucion).Contains(Conmutador) => Resuelto,
                _ => null,
            };

            if (receptor is null || !receptores.Contains(receptor))
                continue;

            yield return new UsoDelConmutador(MotivoDeDescarte(llamada));
        }
    }

    /// <summary>
    /// Null si el resultado de la llamada se usa; si no, el motivo. Se considera descartado: sentencia suelta
    /// (con o sin <c>await</c> ni <c>.ConfigureAwait(false)</c>), <c>_ = ...</c>, y variable asignada que nunca se lee.
    /// Usado no significa que el resultado corte la ruta: eso lo prueba el test de comportamiento de cada una.
    /// </summary>
    private static string? MotivoDeDescarte(InvocationExpressionSyntax llamada)
    {
        SyntaxNode actual = llamada;

        while (true)
        {
            if (actual.Parent is MemberAccessExpressionSyntax { Name.Identifier.Text: "ConfigureAwait" } acceso
                && acceso.Expression == actual && acceso.Parent is InvocationExpressionSyntax configurada)
                actual = configurada;
            else if (actual.Parent is AwaitExpressionSyntax or ParenthesizedExpressionSyntax)
                actual = actual.Parent;
            else
                break;
        }

        switch (actual.Parent)
        {
            case ExpressionStatementSyntax:
                return "descarta el resultado (sentencia suelta)";
            case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarador }:
                return SeLee(declarador.Identifier.Text, declarador) ? null : $"asigna el resultado a '{declarador.Identifier.Text}' y nunca lo lee";
            case AssignmentExpressionSyntax asignacion when asignacion.Right == actual && asignacion.Left is IdentifierNameSyntax destino:
                if (destino.Identifier.Text == "_")
                    return "descarta el resultado (`_ =`)";

                return SeLee(destino.Identifier.Text, asignacion) ? null : $"asigna el resultado a '{destino.Identifier.Text}' y nunca lo lee";
            default:
                return null;
        }
    }

    private static bool SeLee(string variable, SyntaxNode origen)
    {
        var ambito = origen.Ancestors().FirstOrDefault(a =>
            a is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or AccessorDeclarationSyntax)
            ?? origen.SyntaxTree.GetRoot();

        return ambito.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(i => i.Identifier.Text == variable)
            .Any(i => !(i.Parent is AssignmentExpressionSyntax asignacion
                && asignacion.IsKind(SyntaxKind.SimpleAssignmentExpression) && asignacion.Left == i));
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
