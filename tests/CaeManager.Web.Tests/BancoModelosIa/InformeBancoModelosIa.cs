using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaeManager.Web.Tests.BancoModelosIa;

/// <summary>Precio de un modelo en dólares por millón de tokens.</summary>
public sealed record TarifaAnthropic(decimal Entrada, decimal Salida)
{
    public decimal Coste(int tokensEntrada, int tokensSalida) =>
        Math.Round(tokensEntrada / 1_000_000m * Entrada + tokensSalida / 1_000_000m * Salida, 6);
}

/// <summary>
/// Tarifa pública de la API de Anthropic, leída de <c>tarifas-anthropic.json</c>
/// (junto a este fichero), que guarda la fecha y la página de donde se tomó.
/// Con ella se CALCULA el coste de cada caso a partir de los tokens que la
/// API MIDE: el gasto real es el de la consola de Anthropic. Un modelo que no
/// esté en el fichero se mide igual, sin coste, salvo que la ejecución dé su
/// precio.
/// </summary>
public static class TarifasAnthropic
{
    private sealed record Fichero(string FechaDeConsulta, string Fuente, Dictionary<string, TarifaAnthropic> Modelos);

    private static readonly Lazy<Fichero> Leido = new(() =>
        JsonSerializer.Deserialize<Fichero>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "BancoModelosIa", "tarifas-anthropic.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!);

    public static string FechaDeConsulta => Leido.Value.FechaDeConsulta;

    public static string Fuente => Leido.Value.Fuente;

    public static TarifaAnthropic? Para(ParametrosBanco parametros) =>
        parametros is { PrecioEntrada: { } entrada, PrecioSalida: { } salida }
            ? new TarifaAnthropic(entrada, salida)
            : Leido.Value.Modelos.GetValueOrDefault(parametros.Modelo);
}

/// <summary>
/// Agregado de una ruta: la fila de la tabla ruta × modelo × esfuerzo.
/// <paramref name="CostePorMilUsd"/> es la proyección «coste de mil
/// documentos como los del corpus», el coste medio por caso por mil.
/// </summary>
public sealed record ResumenRuta(
    RutaIa Ruta, int Casos, double PuntuacionMedia, int CasosPerfectos, int CasosConErrorDeServicio,
    IReadOnlyDictionary<NivelCaso, double> PuntuacionPorNivel,
    long LatenciaMedianaMs, long LatenciaMaximaMs, int TokensEntrada, int TokensSalida,
    decimal? CosteUsd, decimal? CostePorCasoUsd, decimal? CostePorMilUsd,
    IReadOnlyDictionary<string, int> StopReasons);

/// <summary>Gasto calculado del mes según el libro de gasto local, frente al presupuesto mensual.</summary>
public sealed record GastoDelMes(string Mes, decimal AcumuladoUsd, decimal PresupuestoUsd);

public sealed record InformeBanco(
    DateTimeOffset Fecha, string? Commit, string Modelo, string? EsfuerzoPedido, IReadOnlyList<string> EsfuerzosEnviados,
    IReadOnlyList<string> ModelosQueRespondieron, TarifaAnthropic? Tarifa, string TarifaConsultadaEl, decimal? CosteTotalUsd,
    IReadOnlyList<string> ProblemasDelInstrumento, IReadOnlyList<string> CasosNoConstruibles,
    IReadOnlyList<ResumenRuta> Rutas, IReadOnlyList<ResultadoCaso> Casos, GastoDelMes? GastoDelMes = null);

public static class InformeBancoModelosIa
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static InformeBanco Construir(
        ParametrosBanco parametros, IReadOnlyList<ResultadoCaso> resultados, IReadOnlyList<string> problemas,
        IReadOnlyList<string> casosNoConstruibles, DateTimeOffset fecha, string? commit)
    {
        var rutas = resultados
            .GroupBy(r => r.Ruta)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var latencias = g.Select(r => r.LatenciaMs).OrderBy(l => l).ToList();
                decimal? coste = g.All(r => r.CosteUsd is null) ? null : g.Sum(r => r.CosteUsd ?? 0);
                return new ResumenRuta(
                    g.Key, g.Count(),
                    Math.Round(g.Average(r => r.Puntuacion), 4),
                    g.Count(r => r.Puntuacion >= 1),
                    g.Count(r => r.ErrorServicio is not null),
                    g.GroupBy(r => r.Nivel).OrderBy(n => n.Key).ToDictionary(n => n.Key, n => Math.Round(n.Average(r => r.Puntuacion), 4)),
                    latencias[latencias.Count / 2], latencias[^1],
                    g.Sum(r => r.TokensEntrada), g.Sum(r => r.TokensSalida),
                    coste,
                    coste is null ? null : Math.Round(coste.Value / g.Count(), 6),
                    coste is null ? null : Math.Round(coste.Value / g.Count() * 1000, 2),
                    g.GroupBy(r => r.StopReason ?? "(sin respuesta)").ToDictionary(s => s.Key, s => s.Count()));
            })
            .ToList();

        return new InformeBanco(
            fecha, commit, parametros.Modelo, parametros.Esfuerzo,
            [.. resultados.Select(r => r.EsfuerzoEnviado ?? "(no enviado)").Distinct().Order()],
            [.. resultados.Select(r => r.ModeloQueRespondio).OfType<string>().Distinct().Order()],
            TarifasAnthropic.Para(parametros), TarifasAnthropic.FechaDeConsulta,
            resultados.All(r => r.CosteUsd is null) ? null : resultados.Sum(r => r.CosteUsd ?? 0),
            problemas, casosNoConstruibles, rutas, resultados);
    }

    /// <summary>Escribe <c>.json</c>, <c>.csv</c> y <c>.md</c> con el mismo nombre base y devuelve la ruta del Markdown.</summary>
    public static string Escribir(InformeBanco informe, string carpeta)
    {
        Directory.CreateDirectory(carpeta);
        var nombre = $"banco-ia-{Limpio(informe.Modelo)}-{Limpio(informe.EsfuerzoPedido ?? "por-defecto")}";
        var baseRuta = Path.Combine(carpeta, nombre);

        File.WriteAllText(baseRuta + ".json", JsonSerializer.Serialize(informe, Json), Encoding.UTF8);
        File.WriteAllText(baseRuta + ".csv", Csv(informe), Encoding.UTF8);
        File.WriteAllText(baseRuta + ".md", Markdown(informe), Encoding.UTF8);
        return baseRuta + ".md";
    }

    public static string Limpio(string valor) =>
        new(valor.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_').ToArray());

    public static string Csv(InformeBanco informe)
    {
        var csv = new StringBuilder(
            "modelo,esfuerzo_pedido,esfuerzo_enviado,ruta,caso,nivel,puntuacion,comprobaciones_correctas,comprobaciones_totales," +
            "error_servicio,estado_http,latencia_ms,intentos,tokens_entrada,tokens_salida,stop_reason,coste_usd,max_tokens_enviado,modelo_que_respondio\n");

        foreach (var r in informe.Casos)
        {
            csv.AppendLine(string.Join(',', new[]
            {
                informe.Modelo, informe.EsfuerzoPedido ?? "", r.EsfuerzoEnviado ?? "", r.Ruta.ToString(), r.Caso, r.Nivel.ToString(),
                r.Puntuacion.ToString("0.####", Inv),
                r.ComprobacionesCorrectas.ToString(Inv), r.ComprobacionesTotales.ToString(Inv),
                r.ErrorServicio ?? "", r.EstadoHttp.ToString(Inv), r.LatenciaMs.ToString(Inv),
                r.Intentos.ToString(Inv), r.TokensEntrada.ToString(Inv),
                r.TokensSalida.ToString(Inv), r.StopReason ?? "",
                r.CosteUsd?.ToString("0.######", Inv) ?? "",
                r.MaxTokensEnviado?.ToString(Inv) ?? "", r.ModeloQueRespondio ?? "",
            }));
        }

        return csv.ToString();
    }

    private static string Dolares(decimal? importe, string formato = "0.0000") => importe is { } v ? v.ToString(formato, Inv) : "—";

    public static string Markdown(InformeBanco informe)
    {
        var md = new StringBuilder();
        md.AppendLine(Inv, $"### Banco de modelos de IA — `{informe.Modelo}`, esfuerzo `{informe.EsfuerzoPedido ?? "por defecto del producto"}`");
        md.AppendLine();
        md.AppendLine(Inv, $"- Esfuerzo que viajó en la petición: {string.Join(", ", informe.EsfuerzosEnviados)}");
        md.AppendLine(Inv, $"- Modelo que respondió: {(informe.ModelosQueRespondieron.Count == 0 ? "(ninguno)" : string.Join(", ", informe.ModelosQueRespondieron))}");
        md.AppendLine(Inv, $"- Commit: {informe.Commit ?? "(local)"} · {informe.Fecha:yyyy-MM-dd HH:mm} UTC");

        if (informe.ProblemasDelInstrumento.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("**⚠ Medición NO válida para comparar:**");
            foreach (var problema in informe.ProblemasDelInstrumento)
                md.AppendLine(Inv, $"- {problema}");
        }

        foreach (var aviso in informe.CasosNoConstruibles)
            md.AppendLine(Inv, $"- No ejecutado: {aviso}");

        md.AppendLine(Inv, $"- Tope de salida que viajó (`max_tokens`): {string.Join(", ", informe.Casos.Where(c => c.MaxTokensEnviado is not null).Select(c => c.MaxTokensEnviado).Distinct().Order())}");
        var truncados = informe.Casos.Where(c => c.StopReason == "max_tokens").Select(c => c.Caso).ToList();
        if (truncados.Count > 0)
        {
            md.AppendLine(Inv,
                $"- **Respuestas truncadas por el tope de salida ({truncados.Count})**: {string.Join(", ", truncados)}. Su nota mide el tope, no el modelo: el razonamiento cuenta contra `max_tokens`, así que no compares esfuerzos en estos casos sin subirlo (`--max-tokens`).");
        }

        md.AppendLine("- Una sola respuesta por caso: una diferencia entre dos ejecuciones menor que un caso de la ruta está dentro del ruido.");

        md.AppendLine();
        md.AppendLine("#### Calidad y latencia");
        md.AppendLine();
        md.AppendLine("| Ruta | Casos | Puntuación | Perfectos | Sencillo | Medio | Difícil | Adversarial | Latencia mediana | Latencia máx. | stop_reason |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var r in informe.Rutas)
        {
            string Nivel(NivelCaso nivel) => r.PuntuacionPorNivel.TryGetValue(nivel, out var p) ? p.ToString("0.00", Inv) : "—";
            md.AppendLine(Inv,
                $"| {r.Ruta} | {r.Casos} | {r.PuntuacionMedia:0.00} | {r.CasosPerfectos}/{r.Casos} | {Nivel(NivelCaso.Sencillo)} | {Nivel(NivelCaso.Medio)} | {Nivel(NivelCaso.Dificil)} | {Nivel(NivelCaso.Adversarial)} | {r.LatenciaMedianaMs} ms | {r.LatenciaMaximaMs} ms | {string.Join(", ", r.StopReasons.Select(s => $"{s.Key}×{s.Value}"))} |");
        }

        md.AppendLine();
        md.AppendLine("#### Gasto y proyección");
        md.AppendLine();
        md.AppendLine("| Ruta | Casos | Tokens de entrada | Tokens de salida | Coste $ | Coste por caso $ | Proyección: 1.000 documentos $ |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var r in informe.Rutas)
        {
            md.AppendLine(Inv,
                $"| {r.Ruta} | {r.Casos} | {r.TokensEntrada} | {r.TokensSalida} | {Dolares(r.CosteUsd)} | {Dolares(r.CostePorCasoUsd, "0.000000")} | {Dolares(r.CostePorMilUsd, "0.00")} |");
        }

        md.AppendLine(Inv,
            $"| **Total** | {informe.Casos.Count} | {informe.Casos.Sum(c => c.TokensEntrada)} | {informe.Casos.Sum(c => c.TokensSalida)} | **{Dolares(informe.CosteTotalUsd)}** | | |");
        md.AppendLine();
        md.AppendLine("- **Medido**: los tokens de entrada y de salida, que devuelve la API en cada respuesta (los de salida incluyen el razonamiento).");
        md.AppendLine(informe.Tarifa is { } tarifa
            ? string.Create(Inv, $"- **Calculado**: coste = tokens × tarifa ({tarifa.Entrada} $ de entrada y {tarifa.Salida} $ de salida por millón, consultada el {informe.TarifaConsultadaEl}). Contrástalo con la consola de Anthropic: el gasto real es el de allí.")
            : "- **Sin coste calculado**: este modelo no está en `tarifas-anthropic.json`; da su precio con `--precio-entrada` y `--precio-salida`.");
        md.AppendLine("- **Proyección**: coste medio por caso del corpus × 1.000. Vale para documentos y correos del tamaño de los del corpus.");
        if (informe.GastoDelMes is { } gasto)
        {
            md.AppendLine(Inv,
                $"- **Acumulado de {gasto.Mes}** según el libro de gasto local, con esta ejecución: {Dolares(gasto.AcumuladoUsd)} $ de {Dolares(gasto.PresupuestoUsd, "0")} $ ({(gasto.PresupuestoUsd > 0 ? gasto.AcumuladoUsd / gasto.PresupuestoUsd : 0):P1}). Solo cuenta lo que se ha anotado en este equipo.");
        }

        var conFallos = informe.Casos.Where(c => c.Puntuacion < 1).ToList();
        if (conFallos.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("#### Casos con fallos");
            md.AppendLine();
            md.AppendLine("| Caso | Puntuación | Qué falló |");
            md.AppendLine("|---|---:|---|");
            foreach (var c in conFallos)
            {
                var motivo = c.ErrorServicio is not null ? $"el servicio falló: {c.ErrorServicio} (stop_reason {c.StopReason ?? "—"})" : string.Join("; ", c.Fallos);
                md.AppendLine(Inv, $"| {c.Caso} | {c.Puntuacion:0.00} | {motivo.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ")} |");
            }
        }

        return md.ToString();
    }

    /// <summary>
    /// Cota de gasto ANTES de ejecutar, a partir de las peticiones que el
    /// producto construiría (sin enviarlas): entrada acotada por
    /// <see cref="CotaDeTokensDeEntrada"/> y salida acotada por el tope
    /// <c>max_tokens</c> de cada petición, que la API no deja superar.
    /// </summary>
    public static string MarkdownDeEstimacion(ParametrosBanco parametros, IReadOnlyList<ResultadoCaso> peticiones)
    {
        var tarifa = TarifasAnthropic.Para(parametros);
        var md = new StringBuilder();
        md.AppendLine(Inv, $"### Banco de modelos de IA — estimación sin llamadas, `{parametros.Modelo}`");
        md.AppendLine();
        md.AppendLine("| Ruta | Casos | Cota de tokens de entrada | Cota de tokens de salida (max_tokens) | Cota de coste $ |");
        md.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var g in peticiones.GroupBy(p => p.Ruta).OrderBy(g => g.Key))
        {
            var entrada = g.Sum(p => p.TokensEntradaCota);
            var salida = g.Sum(p => p.MaxTokensEnviado ?? 0);
            md.AppendLine(Inv, $"| {g.Key} | {g.Count()} | {entrada} | {salida} | {Dolares(tarifa?.Coste(entrada, salida))} |");
        }

        var totalEntrada = peticiones.Sum(p => p.TokensEntradaCota);
        var totalSalida = peticiones.Sum(p => p.MaxTokensEnviado ?? 0);
        md.AppendLine(Inv, $"| **Total** | {peticiones.Count} | {totalEntrada} | {totalSalida} | **{Dolares(tarifa?.Coste(totalEntrada, totalSalida))}** |");
        md.AppendLine();
        md.AppendLine("- Es una **cota superior**, no una previsión: supone que cada respuesta agota su `max_tokens`. El gasto real suele ser una fracción pequeña.");
        md.AppendLine(tarifa is null
            ? "- Sin tarifa conocida para este modelo: da su precio con `--precio-entrada` y `--precio-salida`."
            : string.Create(Inv, $"- Tarifa: {tarifa.Entrada} $ de entrada y {tarifa.Salida} $ de salida por millón de tokens, consultada el {TarifasAnthropic.FechaDeConsulta}."));
        md.AppendLine("- No se ha hecho ninguna llamada a la API.");
        return md.ToString();
    }
}

/// <summary>
/// Libro de gasto local: un CSV fuera de todo repositorio al que cada
/// ejecución añade una línea por ruta. Es la contabilidad propia con la que
/// contrastar la consola de Anthropic y con la que se calcula el acumulado
/// del mes. Anotar dos veces el mismo informe no duplica líneas.
/// </summary>
public static class LibroDeGasto
{
    public const string Cabecera = "fecha_utc,origen,modelo,esfuerzo_enviado,ruta,casos,tokens_entrada,tokens_salida,coste_usd,commit";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Anotar(InformeBanco informe, string rutaDelLibro, string origen)
    {
        var carpeta = Path.GetDirectoryName(Path.GetFullPath(rutaDelLibro))!;
        Directory.CreateDirectory(carpeta);
        var hayLibro = File.Exists(rutaDelLibro);
        // Una medición es la misma venga de donde venga: el origen (segunda columna) no entra en la comparación,
        // o anotar con --anotar el informe de una ejecución que ya se anotó sola contaría su gasto dos veces.
        var existentes = hayLibro ? File.ReadAllLines(rutaDelLibro).Select(SinOrigen).ToHashSet() : [];

        var nuevas = informe.Rutas
            .Select(r => string.Join(',', new[]
            {
                informe.Fecha.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv), origen, informe.Modelo,
                string.Join('+', informe.EsfuerzosEnviados), r.Ruta.ToString(), r.Casos.ToString(Inv),
                r.TokensEntrada.ToString(Inv), r.TokensSalida.ToString(Inv),
                r.CosteUsd?.ToString("0.######", Inv) ?? "", informe.Commit ?? "",
            }))
            .Where(linea => !existentes.Contains(SinOrigen(linea)))
            .ToList();

        var anotadas = nuevas.Count;
        if (existentes.Count == 0)
            nuevas.Insert(0, Cabecera);

        // Un libro retocado a mano puede no acabar en salto de línea: sin esto la primera línea nueva se pegaría a la última.
        if (hayLibro && File.ReadAllText(rutaDelLibro) is { Length: > 0 } previo && previo[^1] != '\n')
            File.AppendAllText(rutaDelLibro, Environment.NewLine, new UTF8Encoding(false));
        File.AppendAllLines(rutaDelLibro, nuevas, new UTF8Encoding(false));
        return anotadas;
    }

    private static string SinOrigen(string linea)
    {
        var campos = linea.Split(',');
        return campos.Length < 2 ? linea : string.Join(',', campos.Where((_, indice) => indice != 1));
    }

    /// <summary>Suma del coste anotado en el mes de <paramref name="fecha"/> (UTC). Las líneas sin coste (modelo sin tarifa) no suman.</summary>
    public static decimal AcumuladoDelMes(string rutaDelLibro, DateTimeOffset fecha)
    {
        if (!File.Exists(rutaDelLibro))
            return 0;

        var mes = fecha.UtcDateTime.ToString("yyyy-MM", Inv);
        return File.ReadLines(rutaDelLibro)
            .Skip(1)
            .Select(linea => linea.Split(','))
            .Where(campos => campos.Length >= 9 && campos[0].StartsWith(mes, StringComparison.Ordinal))
            .Sum(campos => decimal.TryParse(campos[8], NumberStyles.Number, Inv, out var coste) ? coste : 0);
    }
}
