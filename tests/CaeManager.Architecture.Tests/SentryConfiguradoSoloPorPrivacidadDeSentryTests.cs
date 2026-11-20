using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>PrivacidadDeSentry.Configurar</c> apaga los datos personales por defecto, las transacciones de rendimiento y los
/// logs de Sentry (que no pasan por su <c>SetBeforeSend</c>), y <c>PrivacidadDeSentryTests</c> (Web.Tests) lo prueba
/// con un host real aunque la configuración los encienda. Program.cs lo llama como lo último de <c>UseSentry</c>
/// (<c>ExposicionDeRedDelDespliegueTests</c>). Esto fija lo que esos dos tests no ven: que ningún otro sitio de
/// <c>src</c> inicia Sentry por su cuenta ni vuelve a encender esas opciones después (un <c>PostConfigure</c> o una
/// asignación fuera del bloque correría tras Configurar), ni da a las transacciones o a los logs un filtro propio. El
/// único filtro de transacciones es el de Configurar, que las descarta todas. Los controles prueban que cada expresión
/// casa con lo que debe, también con <c>??=</c>, <c>AddOptions&lt;…&gt;().PostConfigure</c> y <c>ConfigureOptions</c>.
/// </summary>
public class SentryConfiguradoSoloPorPrivacidadDeSentryTests
{
    // «=» o «??=», no «==»: las dos asignan la opción.
    private static readonly Regex OpcionApagada = new(
        @"\.(SendDefaultPii|TracesSampleRate|TracesSampler|ProfilesSampleRate|EnableLogs|MaxRequestBodySize|IncludeActivityData)\s*(\?\?)?=(?!=)",
        RegexOptions.CultureInvariant);

    private static readonly Regex FiltroSinTratamiento = new(
        @"\b(SetBeforeSendTransaction|SetBeforeSendLog)\s*\(", RegexOptions.CultureInvariant);

    // Configure<SentryOptions>(…) y compañía, AddOptions<SentryOptions>().Configure/PostConfigure(…), una clase
    // IConfigureOptions<SentryOptions> o IPostConfigureOptions<SentryOptions>, y ConfigureOptions<T>() o
    // ConfigureOptions(…), cuyo tipo no tiene por qué nombrar a Sentry (src no lo usa para nada más).
    private static readonly Regex OpcionesReconfiguradas = new(
        @"\b(Configure|PostConfigure|ConfigureAll|PostConfigureAll|AddOptions|IConfigureOptions|IPostConfigureOptions)\s*<\s*(Sentry\.)?(AspNetCore\.)?Sentry\w*Options\s*>"
        + @"|\bConfigureOptions\s*[<(]",
        RegexOptions.CultureInvariant);

    private static readonly Regex InicioDelSdk = new(@"\b(UseSentry|AddSentry|SentrySdk\.Init)\s*\(", RegexOptions.CultureInvariant);

    // La única línea de PrivacidadDeSentry.cs que puede casar con FiltroSinTratamiento: el descarte de toda transacción.
    private const string DescarteDeTransacciones = "opciones.SetBeforeSendTransaction(static (_, _) => null);";

    // Asignaciones con el mismo nombre que no son de Sentry: el tope de cuerpo de Kestrel (IHttpMaxRequestBodySizeFeature).
    private static readonly string[] AsignacionesAjenasASentry =
    [
        "src/CaeManager.Web/Features/Contacto/SolicitudDemoEndpoints.cs: .MaxRequestBodySize =",
    ];

    [Fact]
    public void Solo_privacidad_de_sentry_fija_pii_trazas_y_logs_y_solo_program_inicia_el_sdk()
    {
        var raiz = RaizDelRepositorio();
        var privacidad = Path.Combine(raiz, "src", "CaeManager.Web", "PrivacidadDeSentry.cs");
        var program = Path.Combine(raiz, "src", "CaeManager.Web", "Program.cs");

        var fuentes = Directory.EnumerateFiles(Path.Combine(raiz, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        fuentes.Should().Contain(privacidad).And.Contain(program, "control: el recorrido ve los dos ficheros que configuran Sentry");

        var hallazgos = fuentes
            .SelectMany(f => Hallazgos(Path.GetRelativePath(raiz, f).Replace('\\', '/'), File.ReadAllText(f)))
            .ToList();
        hallazgos.Should().BeEmpty("solo PrivacidadDeSentry.Configurar fija esas opciones y solo Program.cs inicia Sentry, con ella");

        InicioDelSdk.Matches(SinComentariosDeLinea(File.ReadAllText(program))).Select(m => m.Value)
            .Should().Equal(["UseSentry("], "Program.cs inicia Sentry una sola vez, en el bloque que termina en Configurar");

        var textoPrivacidad = File.ReadAllText(privacidad);
        foreach (var asignacion in (string[])
                 [
                     "opciones.SendDefaultPii = false;", "opciones.TracesSampleRate = null;",
                     "opciones.TracesSampler = null;", "opciones.EnableLogs = false;",
                     "aspNetCore.MaxRequestBodySize = RequestSize.None;", "aspNetCore.IncludeActivityData = false;",
                     DescarteDeTransacciones,
                 ])
            textoPrivacidad.Should().Contain(asignacion);
    }

    [Fact]
    public void Control_opcion_apagada_encuentra_exactamente_las_asignaciones_de_privacidad_de_sentry()
    {
        var texto = SinComentariosDeLinea(File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "PrivacidadDeSentry.cs")));

        OpcionApagada.Matches(texto).Select(m => m.Groups[1].Value).Should().Equal(
            "SendDefaultPii", "TracesSampleRate", "TracesSampler", "EnableLogs", "MaxRequestBodySize", "IncludeActivityData");
        FiltroSinTratamiento.Matches(texto).Select(m => m.Value).Should().Equal(["SetBeforeSendTransaction("],
            "control: la expresión ve el descarte de transacciones, la única línea que se le permite a ese fichero");
    }

    [Theory]
    [InlineData("o.TracesSampleRate = 1.0;")]
    [InlineData("o.TracesSampleRate ??= 1.0;")]
    [InlineData("opciones.SendDefaultPii ??= true;")]
    [InlineData("o.MaxRequestBodySize = RequestSize.Always;")]
    [InlineData("o.IncludeActivityData = true;")]
    [InlineData("o.ProfilesSampleRate = 1.0;")]
    [InlineData("o.SetBeforeSendTransaction(t => t);")]
    [InlineData("o.SetBeforeSendLog(l => l);")]
    [InlineData("services.Configure<SentryOptions>(o => o.Debug = true);")]
    [InlineData("services.PostConfigure<Sentry.AspNetCore.SentryAspNetCoreOptions>(o => { });")]
    [InlineData("services.AddOptions<SentryAspNetCoreOptions>().PostConfigure(o => { });")]
    [InlineData("services.AddOptions<Sentry.SentryOptions>().Configure(o => { });")]
    [InlineData("services.ConfigureOptions<OpcionesPropiasDeSentry>();")]
    [InlineData("services.ConfigureOptions(typeof(OpcionesPropiasDeSentry));")]
    [InlineData("sealed class Opciones : IPostConfigureOptions<SentryAspNetCoreOptions>")]
    [InlineData("sealed class Opciones : IConfigureOptions<SentryOptions>")]
    [InlineData("SentrySdk.Init(o => { });")]
    [InlineData("builder.WebHost.UseSentry(o => { });")]
    public void Control_las_expresiones_casan_lo_que_reconfigura_sentry_en_otro_fichero(string linea)
    {
        Hallazgos("src/CaeManager.Infrastructure/Otro.cs", linea).Should().NotBeEmpty(linea);
    }

    [Theory]
    [InlineData("if (o.TracesSampleRate == null) { }")]
    [InlineData("services.Configure<DokifyOptions>(o => { });")]
    [InlineData("services.AddOptions<SmtpOptions>().Configure(o => { });")]
    [InlineData("// o.TracesSampleRate = 1.0;")]
    [InlineData("tope.MaxRequestBodySize = TopeKestrel;")]
    public void Control_las_expresiones_no_casan_lo_que_no_reconfigura_sentry(string linea)
    {
        var relativa = linea.Contains("MaxRequestBodySize", StringComparison.Ordinal)
            ? "src/CaeManager.Web/Features/Contacto/SolicitudDemoEndpoints.cs"
            : "src/CaeManager.Infrastructure/Otro.cs";
        Hallazgos(relativa, linea).Should().BeEmpty(linea);
    }

    [Fact]
    public void Control_en_privacidad_de_sentry_solo_se_permite_el_descarte_de_transacciones()
    {
        const string privacidad = "src/CaeManager.Web/PrivacidadDeSentry.cs";
        Hallazgos(privacidad, DescarteDeTransacciones).Should().BeEmpty();
        Hallazgos(privacidad, "opciones.SetBeforeSendTransaction(static (t, _) => t);").Should().NotBeEmpty();
        Hallazgos(privacidad, "opciones.SetBeforeSendLog(static (l, _) => l);").Should().NotBeEmpty();
    }

    /// <summary>Lo que el recorrido de <c>src</c> señala en un fichero, por su ruta relativa con «/».</summary>
    private static IEnumerable<string> Hallazgos(string relativa, string fuente)
    {
        var texto = SinComentariosDeLinea(fuente);
        var esPrivacidad = relativa == "src/CaeManager.Web/PrivacidadDeSentry.cs";
        var hallazgos = new List<string>();

        if (!esPrivacidad)
            hallazgos.AddRange(OpcionApagada.Matches(texto).Select(m => $"{relativa}: {m.Value}")
                .Where(h => !AsignacionesAjenasASentry.Contains(h.TrimEnd())));

        var sinDescarte = esPrivacidad ? texto.Replace(DescarteDeTransacciones, string.Empty, StringComparison.Ordinal) : texto;
        hallazgos.AddRange(FiltroSinTratamiento.Matches(sinDescarte).Select(m => $"{relativa}: {m.Value}"));
        hallazgos.AddRange(OpcionesReconfiguradas.Matches(texto).Select(m => $"{relativa}: {m.Value}"));
        if (relativa != "src/CaeManager.Web/Program.cs")
            hallazgos.AddRange(InicioDelSdk.Matches(texto).Select(m => $"{relativa}: {m.Value}"));
        return hallazgos;
    }

    // Los comentarios citan UseSentry(...) o las opciones al explicarlas: solo cuenta el código.
    private static string SinComentariosDeLinea(string texto) =>
        string.Join('\n', texto.Split('\n').Where(linea => !linea.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string RaizDelRepositorio()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.GetFiles("*.slnx").Any() && !dir.GetFiles("*.sln").Any())
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }
}
