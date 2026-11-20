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
/// asignación fuera del bloque correría tras Configurar), ni da a las transacciones o a los logs un filtro propio.
/// </summary>
public class SentryConfiguradoSoloPorPrivacidadDeSentryTests
{
    private static readonly Regex OpcionApagada = new(
        @"\.(SendDefaultPii|TracesSampleRate|TracesSampler|ProfilesSampleRate|EnableLogs)\s*=(?!=)", RegexOptions.CultureInvariant);

    private static readonly Regex FiltroSinTratamiento = new(
        @"\b(SetBeforeSendTransaction|SetBeforeSendLog)\s*\(", RegexOptions.CultureInvariant);

    private static readonly Regex OpcionesReconfiguradas = new(
        @"\b(Configure|PostConfigure|ConfigureAll|PostConfigureAll)\s*<\s*(Sentry\.)?(AspNetCore\.)?Sentry\w*Options\s*>", RegexOptions.CultureInvariant);

    private static readonly Regex InicioDelSdk = new(@"\b(UseSentry|AddSentry|SentrySdk\.Init)\s*\(", RegexOptions.CultureInvariant);

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

        var hallazgos = new List<string>();
        foreach (var fuente in fuentes)
        {
            var texto = SinComentariosDeLinea(File.ReadAllText(fuente));
            var relativa = Path.GetRelativePath(raiz, fuente);
            if (fuente != privacidad)
                hallazgos.AddRange(OpcionApagada.Matches(texto).Select(m => $"{relativa}: {m.Value}"));
            hallazgos.AddRange(FiltroSinTratamiento.Matches(texto).Select(m => $"{relativa}: {m.Value}"));
            hallazgos.AddRange(OpcionesReconfiguradas.Matches(texto).Select(m => $"{relativa}: {m.Value}"));
            if (fuente != program)
                hallazgos.AddRange(InicioDelSdk.Matches(texto).Select(m => $"{relativa}: {m.Value}"));
        }

        hallazgos.Should().BeEmpty("solo PrivacidadDeSentry.Configurar fija esas opciones y solo Program.cs inicia Sentry, con ella");

        InicioDelSdk.Matches(SinComentariosDeLinea(File.ReadAllText(program))).Select(m => m.Value)
            .Should().Equal(["UseSentry("], "Program.cs inicia Sentry una sola vez, en el bloque que termina en Configurar");

        var textoPrivacidad = File.ReadAllText(privacidad);
        foreach (var asignacion in (string[])
                 [
                     "opciones.SendDefaultPii = false;", "opciones.TracesSampleRate = null;",
                     "opciones.TracesSampler = null;", "opciones.EnableLogs = false;",
                 ])
            textoPrivacidad.Should().Contain(asignacion);
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
