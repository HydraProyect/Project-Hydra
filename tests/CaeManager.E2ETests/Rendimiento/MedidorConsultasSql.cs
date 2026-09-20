using System.Text.RegularExpressions;

namespace CaeManager.E2ETests.Rendimiento;

/// <summary>
/// Fixture con el log de comandos de EF Core encendido. El servidor de los E2E
/// es un proceso aparte (ver <see cref="WebAppFixture"/>): un interceptor o un
/// contador en memoria del test no lo ve. Lo único que cruza esa frontera es
/// lo que el propio servidor escribe, y con
/// <c>Microsoft.EntityFrameworkCore.Database.Command</c> en Information
/// escribe una entrada «Executed DbCommand» por cada comando SQL que ejecuta
/// (en producción el override está en Warning, ver appsettings.json: solo
/// esta fixture lo sube). Colección propia: subir el nivel llena el log y no
/// tiene por qué pagarlo el resto de la suite.
/// </summary>
public sealed class WebAppFixtureConConsultasSql : WebAppFixture
{
    protected override IReadOnlyDictionary<string, string> VariablesDeEntornoAdicionales() =>
        new Dictionary<string, string>
        {
            ["Serilog__MinimumLevel__Override__Microsoft.EntityFrameworkCore.Database.Command"] = "Information",
        };
}

[CollectionDefinition("AppCollectionConsultasSql")]
public class AppCollectionConsultasSql : ICollectionFixture<WebAppFixtureConConsultasSql>;

/// <summary>
/// Cuenta los comandos SQL que el servidor ejecutó entre dos instantes, leyendo
/// el fichero de log de SU fixture (<see cref="WebAppFixture.PatronFicheroLog"/>,
/// un fichero por fixture desde REC-216).
///
/// <para>
/// <b>Qué mide y qué no.</b> Mide comandos SQL ejecutados por el proceso, sin
/// distinguir si vinieron de la pasada de prerender o del circuito: esa
/// distinción es precisamente lo que el patrón de estado persistido cambia, y
/// medirla por separado exigiría un identificador de petición que el log de EF
/// no lleva. Por eso la métrica de cada pantalla se acota a la consulta propia
/// de la pantalla (<see cref="Contar"/> con un fragmento de SQL de su tabla
/// principal): el resto —layout, notificaciones, servicios de fondo— es ruido
/// de fondo que no cambia con el patrón y se separa así.
/// </para>
///
/// <para>
/// <b>Límite conocido.</b> Lee el fichero fijado en la marca: una medición
/// que cruce la medianoche (rotación diaria de Serilog) lanza en vez de
/// devolver un recuento incompleto, que pasaría el «≤ prerender» por defecto.
/// </para>
/// </summary>
public sealed partial class MedidorConsultasSql(WebAppFixture fixture)
{
    private const string MarcaDeComando = "Executed DbCommand";

    public readonly record struct Marca(string? Fichero, long Desplazamiento);

    /// <summary>Una entrada «Executed DbCommand»: su SQL (todas las líneas tras la cabecera).</summary>
    public sealed record ComandoSql(string Sql)
    {
        public bool EsAjustePorTenant => Sql.Contains("set_config", StringComparison.OrdinalIgnoreCase);
    }

    public Marca MarcarAhora()
    {
        var fichero = FicheroVigente();
        return new Marca(fichero, fichero is null ? 0 : new FileInfo(fichero).Length);
    }

    /// <summary>
    /// Espera a que el log deje de crecer durante <paramref name="silencio"/>:
    /// una navegación mejorada termina su segunda pasada (la del circuito)
    /// DESPUÉS de que el DOM parezca asentado, y NetworkIdle no la ve porque
    /// el circuito habla por WebSocket.
    /// </summary>
    public async Task EsperarSilencioAsync(TimeSpan silencio, TimeSpan presupuesto)
    {
        var limite = DateTime.UtcNow + presupuesto;
        var ultimo = -1L;
        var desde = DateTime.UtcNow;
        while (DateTime.UtcNow < limite)
        {
            var fichero = FicheroVigente();
            var tamano = fichero is null ? 0 : new FileInfo(fichero).Length;
            if (tamano != ultimo)
            {
                ultimo = tamano;
                desde = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - desde >= silencio)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"El log del servidor no se quedó quieto {silencio.TotalMilliseconds} ms en {presupuesto.TotalSeconds} s: el servidor sigue trabajando y el recuento no sería de una navegación completa.");
    }

    public IReadOnlyList<ComandoSql> ComandosDesde(Marca marca)
    {
        var fichero = marca.Fichero ?? FicheroVigente();
        if (fichero is null)
            return [];

        // Si el log rotó desde la marca, lo escrito después vive en otro
        // fichero y este recuento saldría bajo: eso daría verde por defecto.
        // Se para en vez de medir a medias.
        var vigente = FicheroVigente();
        if (marca.Fichero is not null && vigente is not null
            && !string.Equals(vigente, marca.Fichero, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"El log rotó durante la medición ({Path.GetFileName(marca.Fichero)} → {Path.GetFileName(vigente)}): " +
                "el recuento estaría incompleto. Repite la medición.");
        }

        string texto;
        using (var flujo = new FileStream(fichero, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            flujo.Seek(marca.Desplazamiento, SeekOrigin.Begin);
            using var lector = new StreamReader(flujo);
            texto = lector.ReadToEnd();
        }

        var comandos = new List<ComandoSql>();
        foreach (var entrada in EntradasDeLog(texto))
        {
            var lineas = entrada.Split('\n');
            if (!lineas[0].Contains(MarcaDeComando, StringComparison.Ordinal))
                continue;

            // EF parte el SQL en varias líneas; se compacta para que un
            // fragmento como «SELECT count(*)::int FROM "Empresas"» case.
            comandos.Add(new ComandoSql(EspaciosRepetidos().Replace(string.Join(' ', lineas.Skip(1)), " ").Trim()));
        }

        return comandos;
    }

    /// <summary>
    /// Comandos SQL cuyo texto contiene <paramref name="fragmentoSql"/>,
    /// sin contar los <c>set_config</c> del tenant.
    /// </summary>
    public static int Contar(IEnumerable<ComandoSql> comandos, string fragmentoSql) =>
        comandos.Count(c => !c.EsAjustePorTenant && c.Sql.Contains(fragmentoSql, StringComparison.Ordinal));

    private string? FicheroVigente()
    {
        if (!Directory.Exists(fixture.DirectorioLogs))
            return null;

        return Directory.GetFiles(fixture.DirectorioLogs, fixture.PatronFicheroLog)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    // Serilog escribe cada evento empezando por «2026-09-20 12:34:56.789 +02:00 [INF]».
    // Las líneas de SQL que siguen no empiezan así.
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} ", RegexOptions.Multiline)]
    private static partial Regex InicioDeEntrada();

    [GeneratedRegex(@"\s+")]
    private static partial Regex EspaciosRepetidos();

    private static IEnumerable<string> EntradasDeLog(string texto)
    {
        var inicios = InicioDeEntrada().Matches(texto);
        for (var i = 0; i < inicios.Count; i++)
        {
            var desde = inicios[i].Index;
            var hasta = i + 1 < inicios.Count ? inicios[i + 1].Index : texto.Length;
            yield return texto[desde..hasta].TrimEnd('\r', '\n').Replace("\r", string.Empty);
        }
    }
}
