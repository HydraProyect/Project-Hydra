using System.Text.RegularExpressions;

namespace CaeManager.E2ETests.Rendimiento;

/// <summary>
/// Cuenta los comandos SQL que el servidor ejecutó entre dos instantes, leyendo
/// los ficheros de log de su fixture (patrón exclusivo,
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
/// Lee desde la marca y continúa por los ficheros posteriores en el orden
/// de nombres diarios de Serilog. Una pérdida, truncado o nueva rotación
/// durante la lectura lanza para no devolver un recuento incompleto.
/// </para>
/// </summary>
public sealed partial class MedidorConsultasSql(string directorioLogs, string patronFicheroLog)
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
        var ultimo = (Fichero: (string?)null, Tamano: -1L);
        var desde = DateTime.UtcNow;
        while (DateTime.UtcNow < limite)
        {
            var fichero = FicheroVigente();
            var tamano = fichero is null ? 0 : new FileInfo(fichero).Length;
            if ((fichero, tamano) != ultimo)
            {
                ultimo = (fichero, tamano);
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
        var ficheros = FicherosOrdenados();
        var inicio = marca.Fichero is null ? 0 : Array.FindIndex(ficheros,
            f => string.Equals(f, marca.Fichero, StringComparison.OrdinalIgnoreCase));
        if (inicio < 0)
            throw new InvalidOperationException("El fichero de la marca desapareció: el recuento estaría incompleto.");

        var texto = new System.Text.StringBuilder();
        for (var i = inicio; i < ficheros.Length; i++)
        {
            using var flujo = new FileStream(ficheros[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var desplazamiento = i == inicio ? marca.Desplazamiento : 0;
            if (flujo.Length < desplazamiento)
                throw new InvalidOperationException("El log se truncó desde la marca: el recuento estaría incompleto.");
            flujo.Seek(desplazamiento, SeekOrigin.Begin);
            using var lector = new StreamReader(flujo);
            texto.AppendLine(lector.ReadToEnd());
        }

        if (!ficheros.SequenceEqual(FicherosOrdenados(), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Los ficheros de log cambiaron durante la lectura: repite la medición.");

        var comandos = new List<ComandoSql>();
        foreach (var entrada in EntradasDeLog(texto.ToString()))
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

    private string? FicheroVigente() => FicherosOrdenados().LastOrDefault();

    private string[] FicherosOrdenados() => Directory.Exists(directorioLogs)
        ? Directory.GetFiles(directorioLogs, patronFicheroLog).Order(StringComparer.Ordinal).ToArray()
        : [];

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
