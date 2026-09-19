namespace CaeManager.E2ETests;

/// <summary>
/// REC-216. Cada fixture arranca su propio proceso de CaeManager.Web y xUnit
/// paraleliza por colección, así que con la ruta de log por defecto los
/// cuatro procesos escribían en el MISMO <c>log-AAAAMMDD.txt</c>, sin puerto,
/// PID ni nombre de test en ninguna línea (medido en el run 35402030148 de
/// CI: cuatro arranques solapados en un fichero de 11.459 líneas). Estos
/// tests fijan que cada fixture deja su rastro en un fichero propio,
/// nombrado por su tipo (ver <see cref="WebAppFixture.PatronFicheroLog"/>).
///
/// <para>
/// Qué observan: que existe un fichero con el patrón de ESTA fixture y que
/// contiene la línea con que arranca todo proceso (siembra de la identidad
/// raíz). Qué NO observan, a propósito: exclusividad ("ningún otro proceso
/// escribió aquí"). En CI el directorio empieza vacío, pero en una máquina
/// de desarrollo los ficheros del mismo día se acumulan entre ejecuciones y
/// una aserción de exclusividad daría falsos rojos. La exclusividad la
/// garantiza la ruta por proceso; lo que sí puede romperse en silencio es que
/// alguien la quite, y eso sí lo detecta: sin la variable, el proceso vuelve
/// a <c>log-AAAAMMDD.txt</c> y no aparece ningún fichero con el patrón.
/// (Localmente, borrar <c>App_Data/logs</c> antes de correrlos si se quiere
/// comprobar esa sensibilidad: un fichero de una ejecución anterior también
/// casa con el patrón.)
/// </para>
/// </summary>
internal static class LogPorFixture
{
    private const string LineaDeArranque = "Identidad raíz de plataforma designada";

    internal static async Task AfirmarLogPropioAsync(WebAppFixture fixture)
    {
        var ficheros = Directory.Exists(fixture.DirectorioLogs)
            ? Directory.GetFiles(fixture.DirectorioLogs, fixture.PatronFicheroLog)
            : [];

        Assert.True(
            ficheros.Length > 0,
            $"No hay ningún fichero \"{fixture.PatronFicheroLog}\" en {fixture.DirectorioLogs}: "
            + "el proceso de esta fixture no está escribiendo en su log propio.");

        var contenidos = new List<string>();
        foreach (var ruta in ficheros)
        {
            // Serilog mantiene el fichero abierto mientras la app real corre.
            await using var flujo = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var lector = new StreamReader(flujo);
            contenidos.Add(await lector.ReadToEndAsync());
        }

        Assert.Contains(contenidos, c => c.Contains(LineaDeArranque, StringComparison.Ordinal));
    }
}

[Collection("AppCollection")]
public class LogPorFixtureAppCollectionTests(WebAppFixture fixture)
{
    [Fact]
    public Task La_fixture_compartida_escribe_en_su_propio_fichero_de_log() =>
        LogPorFixture.AfirmarLogPropioAsync(fixture);
}

[Collection("AppCollectionRetencion")]
public class LogPorFixtureRetencionTests(WebAppFixtureConRetencionActiva fixture)
{
    [Fact]
    public Task La_fixture_de_retencion_escribe_en_su_propio_fichero_de_log() =>
        LogPorFixture.AfirmarLogPropioAsync(fixture);
}
