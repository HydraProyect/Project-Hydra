using System.Text.RegularExpressions;
using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.Common;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>D3: ningún Command queda sin clasificar frente al límite del Tenant TEC participante</b> (hallazgo 3 de la ronda 1 del
/// revisor-puente). Dos trinquetes:
/// <list type="bullet">
/// <item><b>Clasificación</b> (<see cref="Todo_Command_esta_clasificado_y_la_clasificacion_es_la_que_aplica_el_codigo"/>): cada
/// Command de Application tiene una línea en <c>Datos/LimiteTenantTecClasificacion.txt</c>. Las clases que el código aplica
/// (<c>cerrado-behavior</c>, <c>cerrado-datos</c>, <c>cerrado-handler</c>, <c>pendiente</c> y el <c>abierto</c> por una
/// decisión de <see cref="RegistroLimiteTenantTec.AbiertasAlTenantTec"/>, hoy las Subcontratas por D5) se comprueban contra
/// el código por reflexión; <c>abierto</c> exige motivo y <c>no-aplica</c> es lo que no es un alta de Outbound de D3. Un Command nuevo
/// sin línea da rojo, y una línea que diga «cerrado» sin que el código lo cierre, también.</item>
/// <item><b>Llamadores de las altas</b> (<see cref="Los_llamadores_de_las_altas_de_Outbound_son_los_congelados"/>): quién llama a
/// las factorías de alta de Centros, Clientes empresariales, Relaciones Empresariales, delegaciones, Asignaciones de Operación
/// y de Cartera y tarifas del Operador CAE, congelado en <c>Congelados/LimiteTenantTec-llamadores-de-altas.txt</c>. Una
/// llamada nueva da rojo hasta que alguien la clasifique; si el llamador es un Command, su clase no puede ser
/// <c>no-aplica</c> sin motivo.</item>
/// </list>
///
/// <para>
/// <b>Lo que observa</b>: los tipos de Application, los constructores de sus handlers y el texto de <c>src</c> sin
/// comentarios de línea. <b>Lo que no observa</b>: si la clasificación es acertada (es una decisión: DN-4, DN-7), si el
/// handler llama de verdad a <see cref="IOutboundCerradoTenantTec"/> antes de escribir (lo fijan los tests de Application de
/// cada handler), ni una alta que no pase por las factorías listadas (SQL a mano, seeders nuevos con otra factoría).
/// </para>
/// </summary>
public class LimiteTenantTecClasificacionTests
{
    private const string RutaClasificacion = "tests/CaeManager.Architecture.Tests/Datos/LimiteTenantTecClasificacion.txt";

    private static readonly string[] Clases =
        ["cerrado-behavior", "cerrado-datos", "cerrado-handler", "pendiente", "abierto", "no-aplica"];

    private static readonly Type[] TiposDeApplication = typeof(ICommandBase).Assembly.GetTypes();

    private static readonly List<Type> Commands = TiposDeApplication
        .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false }
                    && typeof(ICommandBase).IsAssignableFrom(t))
        .ToList();

    private static string NombreCorto(Type t) => t.FullName![(t.Namespace!.Length + 1)..];

    /// <summary>
    /// La clase que el código aplica a <paramref name="comando"/>, o <c>null</c> si el código no la cierra ni la deja pendiente
    /// (entonces la lista decide entre <c>abierto</c> y <c>no-aplica</c>). El orden es el del pipeline: el behavior por el
    /// registro, el behavior por los datos de la petición y, si no, el handler.
    /// </summary>
    private static string? ClaseDelCodigo(Type comando)
    {
        if (RegistroLimiteTenantTec.CierraSinLinea(comando))
            return "cerrado-behavior";
        if (typeof(ILimiteTenantTecSegunDatos).IsAssignableFrom(comando))
            return "cerrado-datos";
        if (HandlerConsultaElLimite(comando))
            return "cerrado-handler";
        if (RegistroLimiteTenantTec.EstaPendiente(comando))
            return "pendiente";
        // Abierto por una decisión (D5, Subcontratas): el código lo deja abierto a propósito y la lista no puede decir
        // otra cosa.
        if (RegistroLimiteTenantTec.EstaAbiertaAlTenantTec(comando))
            return "abierto";
        return null;
    }

    /// <summary>Si algún handler de <paramref name="comando"/> recibe <see cref="IOutboundCerradoTenantTec"/>.</summary>
    private static bool HandlerConsultaElLimite(Type comando) =>
        TiposDeApplication
            .Where(h => h.GetInterfaces().Any(i => i.IsGenericType
                                                   && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)
                                                   && i.GetGenericArguments()[0] == comando))
            .Any(h => h.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IOutboundCerradoTenantTec))));

    private sealed record Linea(string Nombre, string Clase, string Motivo);

    private static List<Linea> LeerClasificacion() =>
        File.ReadAllLines(Path.Combine(FuentesDeSrc.RaizDelRepositorio(), RutaClasificacion))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t'))
            .Select(c => new Linea(c[0], c.Length > 1 ? c[1] : "", c.Length > 2 ? c[2] : ""))
            .ToList();

    /// <summary>Los desvíos de <paramref name="lista"/> frente a <paramref name="comandos"/>; vacío si coinciden.</summary>
    private static List<string> Desvios(IReadOnlyList<Linea> lista, IReadOnlyList<Type> comandos)
    {
        var desvios = new List<string>();
        var porNombre = comandos.ToDictionary(NombreCorto, StringComparer.Ordinal);
        var listados = new HashSet<string>(StringComparer.Ordinal);

        foreach (var linea in lista)
        {
            if (!listados.Add(linea.Nombre))
                desvios.Add($"REPETIDA   {linea.Nombre}");
            if (!Clases.Contains(linea.Clase))
                desvios.Add($"CLASE      {linea.Nombre}: «{linea.Clase}» no es una de {string.Join(", ", Clases)}");
            if (!porNombre.TryGetValue(linea.Nombre, out var tipo))
            {
                desvios.Add($"OBSOLETA   {linea.Nombre}: ya no es un Command de Application; borra la línea");
                continue;
            }

            var delCodigo = ClaseDelCodigo(tipo);
            if (delCodigo is not null && linea.Clase != delCodigo)
                desvios.Add($"DISTINTA   {linea.Nombre}: el código lo deja en {delCodigo}, la lista dice {linea.Clase}");
            else if (delCodigo is null && linea.Clase is not ("abierto" or "no-aplica"))
                desvios.Add($"SIN CIERRE {linea.Nombre}: la lista dice {linea.Clase} y el código no lo cierra ni lo deja pendiente");
            if (linea.Clase is "abierto" or "pendiente" && string.IsNullOrWhiteSpace(linea.Motivo))
                desvios.Add($"SIN MOTIVO {linea.Nombre}: «{linea.Clase}» lleva su motivo en la tercera columna");
        }

        foreach (var nombre in porNombre.Keys.Where(n => !listados.Contains(n)).Order(StringComparer.Ordinal))
            desvios.Add($"NUEVO      {nombre}\t{ClaseDelCodigo(porNombre[nombre]) ?? "?"}: clasifícalo en {RutaClasificacion}");

        return desvios;
    }

    [Fact]
    public void Todo_Command_esta_clasificado_y_la_clasificacion_es_la_que_aplica_el_codigo()
    {
        Commands.Should().HaveCountGreaterThan(250, "control positivo: el detector ve los Commands de Application");
        Commands.Select(NombreCorto).Should().OnlyHaveUniqueItems("la lista identifica cada Command por su nombre corto");

        var lista = LeerClasificacion();
        var desvios = Desvios(lista, Commands);
        if (desvios.Count > 0)
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "LimiteTenantTecClasificacion.actual.txt"),
                Commands.Select(c => $"{NombreCorto(c)}\t{ClaseDelCodigo(c) ?? "?"}").Order(StringComparer.Ordinal));

        lista.Should().HaveCountGreaterThan(250, "control positivo: la lista se lee");
        lista.Select(l => l.Nombre).Should().BeInAscendingOrder(StringComparer.Ordinal, "ordenada para que el diff sea legible");
        desvios.Should().BeEmpty(
            "cada Command se clasifica a conciencia frente a D3 (DN-4, DN-7); un Command nuevo, o una clase que el código "
            + "no aplica, es una decisión que se escribe en la lista en el mismo commit");
    }

    /// <summary>
    /// Sensibilidad del instrumento con datos sintéticos (nunca con la propia lista): un Command sin línea, una línea que
    /// dice «cerrado» de un Command que el código no cierra, un abierto sin motivo y un cerrado del registro marcado como
    /// abierto dan rojo cada uno.
    /// </summary>
    [Fact]
    public void El_instrumento_de_clasificacion_detecta_cada_desvio()
    {
        var crearCentro = Commands.Single(c => c.Name == "CrearCentroCommand");
        var editarCentro = Commands.Single(c => c.Name == "EditarCentroCommand");
        var crearEmpresa = Commands.Single(c => c.Name == "CrearEmpresaCommand");
        var fijarTarifa = Commands.Single(c => c.Name == "FijarTarifaOperadorCaeCommand");
        var crearSubcontrata = Commands.Single(c => c.Name == "CrearSubcontrataCommand");
        var editarSubcontrata = Commands.Single(c => c.Name == "EditarSubcontrataCommand");
        var restaurarSubcontrata = Commands.Single(c => c.Name == "RestaurarSubcontrataCommand");
        var notaSubcontrata = Commands.Single(c => c.Name == "GuardarNotaInternaSubcontrataCommand");
        ClaseDelCodigo(crearCentro).Should().Be("cerrado-behavior");
        ClaseDelCodigo(crearEmpresa).Should().Be("cerrado-datos");
        ClaseDelCodigo(fijarTarifa).Should().Be("cerrado-handler");
        // D5 abre las Subcontratas al Tenant TEC salvo la Relación Empresarial con Clientes empresariales (EP-4, que D3
        // aplica, y DN-7), que se cierra como en Empresas: el alta por datos y la edición y la restauración en su handler.
        ClaseDelCodigo(crearSubcontrata).Should().Be("cerrado-datos", "como CrearEmpresaCommand con ClienteIds");
        ClaseDelCodigo(editarSubcontrata).Should().Be("cerrado-handler", "como EditarEmpresaCommandHandler");
        ClaseDelCodigo(restaurarSubcontrata).Should().Be("cerrado-handler", "como RestaurarEmpresaCommandHandler");
        ClaseDelCodigo(notaSubcontrata).Should().Be("abierto", "D5 abre el resto de las Subcontratas al Tenant TEC");
        ClaseDelCodigo(editarCentro).Should().BeNull();
        Commands.Where(RegistroLimiteTenantTec.EstaPendiente).Select(ClaseDelCodigo).Should().NotBeEmpty()
            .And.OnlyContain(c => c == "pendiente", "control positivo de «pendiente»: Comunicaciones espera a DN-5");

        // Una Subcontrata abierta clasificada como no-aplica, o como pendiente, contradice lo que el código abre por D5.
        Desvios([new(NombreCorto(notaSubcontrata), "no-aplica", "")], [notaSubcontrata])
            .Should().ContainSingle().Which.Should().StartWith("DISTINTA");
        Desvios([new(NombreCorto(notaSubcontrata), "pendiente", "D5")], [notaSubcontrata])
            .Should().ContainSingle().Which.Should().StartWith("DISTINTA");
        // Y la que se cierra por datos, clasificada como abierta por D5, también da rojo.
        Desvios([new(NombreCorto(crearSubcontrata), "abierto", "D5")], [crearSubcontrata])
            .Should().ContainSingle().Which.Should().StartWith("DISTINTA");

        Linea Bien(Type t) => new(NombreCorto(t), ClaseDelCodigo(t) ?? "abierto", "motivo");
        Type[] comandos = [crearCentro, editarCentro, fijarTarifa];
        Desvios([Bien(crearCentro), Bien(editarCentro), Bien(fijarTarifa)], comandos).Should().BeEmpty("control positivo");

        Desvios([Bien(crearCentro), Bien(fijarTarifa)], comandos).Should().ContainSingle().Which.Should().StartWith("NUEVO");
        Desvios([Bien(crearCentro), Bien(editarCentro) with { Clase = "cerrado-handler" }, Bien(fijarTarifa)], comandos)
            .Should().ContainSingle().Which.Should().StartWith("SIN CIERRE");
        Desvios([Bien(crearCentro), Bien(editarCentro) with { Motivo = "" }, Bien(fijarTarifa)], comandos)
            .Should().ContainSingle().Which.Should().StartWith("SIN MOTIVO");
        Desvios([Bien(crearCentro) with { Clase = "abierto" }, Bien(editarCentro), Bien(fijarTarifa)], comandos)
            .Should().ContainSingle().Which.Should().StartWith("DISTINTA");
    }

    // ───────────── llamadores de las factorías de alta ─────────────

    /// <summary>
    /// Las altas de D3 y su factoría. Los métodos de instancia van con el punto delante para no contar su declaración; las
    /// sobrecargas internas que se llaman sin receptor quedan dentro del fichero que ya está listado por su llamada pública.
    /// </summary>
    private static readonly (string Simbolo, Regex Patron)[] Altas =
    [
        ("new Centro", new(@"\bnew\s+Centro\s*\(")),
        ("Empresa.CrearComoCliente", new(@"\bEmpresa\.CrearComoCliente\s*\(")),
        (".AgregarSiNoVigenteAsync", new(@"\.AgregarSiNoVigenteAsync\s*\(")),
        ("new DelegacionTenant", new(@"\bnew\s+DelegacionTenant\s*\(")),
        (".AbrirOperacionDelegadaAsync", new(@"\.AbrirOperacionDelegadaAsync\s*\(")),
        (".AbrirCarteraOperadorAsync", new(@"\.AbrirCarteraOperadorAsync\s*\(")),
        (".AsegurarCarteraTenantEnteroAsync", new(@"\.AsegurarCarteraTenantEnteroAsync\s*\(")),
        (".IncorporarAsync", new(@"\.IncorporarAsync\s*\(")),
        (".IncorporarApoyoAsync", new(@"\.IncorporarApoyoAsync\s*\(")),
        (".RelevarPrincipalAsync", new(@"\.RelevarPrincipalAsync\s*\(")),
        ("AsignacionCartera.Externa", new(@"\bAsignacionCartera\.(Externa|Interna)\s*\(")),
        ("TarifaOperadorCae.Registrar", new(@"\bTarifaOperadorCae\.Registrar\s*\(")),
        // Ronda 3 del revisor-puente (EP-4): las factorías que hacen de un Tenant el Operador CAE de otro o amplían lo que
        // hace como tal.
        ("RelacionEmpresarial.Crear", new(@"\bRelacionEmpresarial\.(Crear|Migrar)\s*\(")),
        ("EncargoAdministracion.Registrar", new(@"\bEncargoAdministracion\.(Registrar|SembrarDeDemostracion)\s*\(")),
        ("new AsignacionOperadorDelegado", new(@"\bnew\s+AsignacionOperadorDelegado\s*\(")),
        ("AsignacionOperacion.Externa", new(@"\bAsignacionOperacion\.(Externa|Interna|Raiz)\s*\(")),
    ];

    private static readonly Regex ComentarioDeLinea = new(@"^\s*//|//(?=(?:[^""]*""[^""]*"")*[^""]*$).*");

    private static Dictionary<Ubicacion, int> Medir(IEnumerable<(string Lugar, string Texto)> fuentes)
    {
        var medido = new Dictionary<Ubicacion, int>();
        foreach (var (lugar, texto) in fuentes)
        {
            foreach (var linea in texto.Split('\n'))
            {
                if (linea.TrimStart().StartsWith("//", StringComparison.Ordinal))
                    continue;
                var codigo = ComentarioDeLinea.Replace(linea, "");
                foreach (var (simbolo, patron) in Altas)
                {
                    var n = patron.Matches(codigo).Count;
                    if (n == 0)
                        continue;
                    var clave = new Ubicacion(lugar, simbolo);
                    medido[clave] = medido.GetValueOrDefault(clave) + n;
                }
            }
        }

        return medido;
    }

    private static IEnumerable<(string, string)> FuentesCSharpDeSrc() =>
        FuentesDeSrc.Archivos()
            .Where(a => a.EndsWith(".cs", StringComparison.Ordinal))
            .Select(a => (FuentesDeSrc.Relativa(a), File.ReadAllText(a)));

    [Fact]
    public void Los_llamadores_de_las_altas_de_Outbound_son_los_congelados()
    {
        var medido = Medir(FuentesCSharpDeSrc());

        var fallo = ListaCongelada.Verificar("LimiteTenantTec-llamadores-de-altas", medido,
            "Una llamada nueva a una factoría de alta de Outbound (Centro, Cliente empresarial, Relación Empresarial, "
            + "delegación, Asignación de Operación o de Cartera, tarifa del Operador CAE) es un alta que D3 tiene que cerrar "
            + "al Tenant TEC participante sin línea Outbound (DN-4, DN-7): ciérrala en el behavior (RegistroLimiteTenantTec) o "
            + "en el handler con IOutboundCerradoTenantTec, clasifica su Command en " + RutaClasificacion
            + " y añade la línea aquí en el mismo commit. Si retiras una llamada, baja o borra su línea.");

        fallo.Should().BeNull();
    }

    /// <summary>
    /// Los Commands declarados en un fichero (<c>record</c> o <c>class</c> cuyo nombre acaba en <c>Command</c>). Se leen del
    /// contenido y no del nombre del fichero: un fichero que agrupa varios (Dashboard/TarifasOperadorCae/TarifasOperadorCae.cs
    /// con FijarTarifaOperadorCaeCommand) no se llama como ninguno de ellos (G4 de la ronda 4).
    /// </summary>
    private static readonly Regex DeclaracionDeCommand = new(@"\b(?:record|class)\s+(\w+Command)\b");

    private static IEnumerable<string> CommandsDeclarados(string texto) =>
        texto.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .SelectMany(l => DeclaracionDeCommand.Matches(l).Select(m => m.Groups[1].Value));

    /// <summary>
    /// Un Command declarado en un fichero que llama a una factoría de alta no puede estar en <c>no-aplica</c> sin motivo: o se
    /// cierra, o se deja abierto o fuera con la razón escrita.
    /// </summary>
    [Fact]
    public void Un_Command_que_llama_a_una_factoria_de_alta_no_queda_en_no_aplica_sin_motivo()
    {
        var lista = LeerClasificacion().ToDictionary(l => l.Nombre, StringComparer.Ordinal);
        var fuentes = FuentesCSharpDeSrc().ToList();
        var textoPorLugar = fuentes.ToDictionary(f => f.Item1, f => f.Item2, StringComparer.Ordinal);
        var llamadores = Medir(fuentes).Keys
            .Select(u => u.Lugar)
            .Distinct()
            .SelectMany(lugar => CommandsDeclarados(textoPorLugar[lugar]))
            .Distinct()
            .ToList();
        llamadores.Should().Contain(
            ["CrearCentroCommand", "CrearUsuarioCommand", "AsumirPrincipalDeOperacionCommand", "FijarTarifaOperadorCaeCommand"],
            "control positivo: el detector ve los Commands que llaman a las altas, también los declarados en un fichero que no "
            + "se llama como ellos");

        llamadores.Where(n => !lista.TryGetValue(n, out var l) || (l.Clase == "no-aplica" && string.IsNullOrWhiteSpace(l.Motivo)))
            .Should().BeEmpty("un Command que da de alta algo de Outbound se clasifica con su motivo");
    }

    [Fact]
    public void El_detector_de_Commands_declarados_lee_el_contenido_y_no_el_nombre_del_fichero()
    {
        CommandsDeclarados(
                "public sealed record FijarTarifaOperadorCaeCommand(Guid Id) : ICommand;\n"
                + "public sealed class FijarTarifaOperadorCaeCommandHandler : IRequestHandler<FijarTarifaOperadorCaeCommand, Result>\n"
                + "public record class OtroCommand;\n"
                + "// public record ComentadoCommand;\n"
                + "public class OtroCommandValidator : AbstractValidator<OtroCommand>")
            .Should().Equal("FijarTarifaOperadorCaeCommand", "OtroCommand");
    }

    [Fact]
    public void El_detector_de_altas_cuenta_llamadas_y_no_comentarios_ni_declaraciones()
    {
        var medido = Medir(
        [
            ("a.cs", "var c = new Centro(x);\n// var d = new Centro(y);\n/// <see cref=\"IncorporarAsync(Guid)\"/>\n"
                     + "await catalogo.IncorporarAsync(a, b); // otra .IncorporarAsync(\n"
                     + "public Task<R> IncorporarAsync(Guid a) => null;\n"
                     + "var s = \"no es // un comentario\"; contexto.Agregar(TarifaOperadorCae.Registrar(p));"),
        ]);

        medido.Should().BeEquivalentTo(new Dictionary<Ubicacion, int>
        {
            [new("a.cs", "new Centro")] = 1,
            [new("a.cs", ".IncorporarAsync")] = 1,
            [new("a.cs", "TarifaOperadorCae.Registrar")] = 1,
        });
    }
}
