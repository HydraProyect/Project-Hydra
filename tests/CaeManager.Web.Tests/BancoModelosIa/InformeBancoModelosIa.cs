using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaeManager.Web.Tests.BancoModelosIa;

/// <summary>
/// Tarifa pública de la API de Anthropic en dólares por millón de tokens,
/// consultada el 2026-10-06. Solo sirve para estimar el coste de cada caso:
/// el gasto real es el de la consola de Anthropic. Un modelo que no esté
/// aquí se mide igual, sin coste, salvo que la ejecución dé su precio.
/// </summary>
public sealed record TarifaAnthropic(decimal EntradaPorMillon, decimal SalidaPorMillon)
{
    public decimal Coste(int tokensEntrada, int tokensSalida) =>
        Math.Round(tokensEntrada / 1_000_000m * EntradaPorMillon + tokensSalida / 1_000_000m * SalidaPorMillon, 6);
}

public static class TarifasAnthropic
{
    private static readonly Dictionary<string, TarifaAnthropic> Conocidas = new()
    {
        ["claude-haiku-5-5"] = new(0.10m, 0.50m),
        ["claude-haiku-4-5"] = new(1m, 5m),
        ["claude-sonnet-5-5"] = new(2m, 10m),
        ["claude-sonnet-5"] = new(2m, 10m),
        ["claude-sonnet-4-6"] = new(3m, 15m),
        ["claude-opus-5-5"] = new(4m, 20m),
        ["claude-opus-5"] = new(5m, 25m),
    };

    public static TarifaAnthropic? Para(ParametrosBanco parametros) =>
        parametros is { PrecioEntrada: { } entrada, PrecioSalida: { } salida }
            ? new TarifaAnthropic(entrada, salida)
            : Conocidas.GetValueOrDefault(parametros.Modelo);
}

/// <summary>Agregado de una ruta: la fila de la tabla ruta × modelo × esfuerzo.</summary>
public sealed record ResumenRuta(
    RutaIa Ruta, int Casos, double PuntuacionMedia, int CasosPerfectos, int CasosConErrorDeServicio,
    IReadOnlyDictionary<NivelCaso, double> PuntuacionPorNivel,
    long LatenciaMedianaMs, long LatenciaMaximaMs, int TokensEntrada, int TokensSalida, decimal? CosteUsd,
    IReadOnlyDictionary<string, int> StopReasons);

public sealed record InformeBanco(
    DateTimeOffset Fecha, string? Commit, string Modelo, string? EsfuerzoPedido, IReadOnlyList<string> EsfuerzosEnviados,
    IReadOnlyList<string> ModelosQueRespondieron, TarifaAnthropic? Tarifa, decimal? CosteTotalUsd,
    IReadOnlyList<string> ProblemasDelInstrumento, IReadOnlyList<string> CasosNoConstruibles,
    IReadOnlyList<ResumenRuta> Rutas, IReadOnlyList<ResultadoCaso> Casos);

public static class InformeBancoModelosIa
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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
                return new ResumenRuta(
                    g.Key, g.Count(),
                    Math.Round(g.Average(r => r.Puntuacion), 4),
                    g.Count(r => r.Puntuacion >= 1),
                    g.Count(r => r.ErrorServicio is not null),
                    g.GroupBy(r => r.Nivel).OrderBy(n => n.Key).ToDictionary(n => n.Key, n => Math.Round(n.Average(r => r.Puntuacion), 4)),
                    latencias[latencias.Count / 2], latencias[^1],
                    g.Sum(r => r.TokensEntrada), g.Sum(r => r.TokensSalida),
                    g.All(r => r.CosteUsd is null) ? null : g.Sum(r => r.CosteUsd ?? 0),
                    g.GroupBy(r => r.StopReason ?? "(sin respuesta)").ToDictionary(s => s.Key, s => s.Count()));
            })
            .ToList();

        return new InformeBanco(
            fecha, commit, parametros.Modelo, parametros.Esfuerzo,
            [.. resultados.Select(r => r.EsfuerzoEnviado ?? "(no enviado)").Distinct().Order()],
            [.. resultados.Select(r => r.ModeloQueRespondio).OfType<string>().Distinct().Order()],
            TarifasAnthropic.Para(parametros),
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

    private static string Limpio(string valor) =>
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
                r.Puntuacion.ToString("0.####", CultureInfo.InvariantCulture),
                r.ComprobacionesCorrectas.ToString(CultureInfo.InvariantCulture), r.ComprobacionesTotales.ToString(CultureInfo.InvariantCulture),
                r.ErrorServicio ?? "", r.EstadoHttp.ToString(CultureInfo.InvariantCulture), r.LatenciaMs.ToString(CultureInfo.InvariantCulture),
                r.Intentos.ToString(CultureInfo.InvariantCulture), r.TokensEntrada.ToString(CultureInfo.InvariantCulture),
                r.TokensSalida.ToString(CultureInfo.InvariantCulture), r.StopReason ?? "",
                r.CosteUsd?.ToString("0.######", CultureInfo.InvariantCulture) ?? "",
                r.MaxTokensEnviado?.ToString(CultureInfo.InvariantCulture) ?? "", r.ModeloQueRespondio ?? "",
            }));
        }

        return csv.ToString();
    }

    public static string Markdown(InformeBanco informe)
    {
        var inv = CultureInfo.InvariantCulture;
        var md = new StringBuilder();
        md.AppendLine(inv, $"### Banco de modelos de IA — `{informe.Modelo}`, esfuerzo `{informe.EsfuerzoPedido ?? "por defecto del producto"}`");
        md.AppendLine();
        md.AppendLine(inv, $"- Esfuerzo que viajó en la petición: {string.Join(", ", informe.EsfuerzosEnviados)}");
        md.AppendLine(inv, $"- Modelo que respondió: {(informe.ModelosQueRespondieron.Count == 0 ? "(ninguno)" : string.Join(", ", informe.ModelosQueRespondieron))}");
        md.AppendLine(inv, $"- Coste estimado: {(informe.CosteTotalUsd is { } total ? total.ToString("0.0000", inv) + " $" : "sin tarifa conocida para este modelo")}");
        md.AppendLine(inv, $"- Commit: {informe.Commit ?? "(local)"} · {informe.Fecha:yyyy-MM-dd HH:mm} UTC");

        if (informe.ProblemasDelInstrumento.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("**⚠ Medición NO válida para comparar:**");
            foreach (var problema in informe.ProblemasDelInstrumento)
                md.AppendLine(inv, $"- {problema}");
        }

        foreach (var aviso in informe.CasosNoConstruibles)
            md.AppendLine(inv, $"- No ejecutado: {aviso}");

        md.AppendLine();
        md.AppendLine("| Ruta | Casos | Puntuación | Perfectos | Sencillo | Medio | Difícil | Adversarial | Latencia mediana | Latencia máx. | Tokens ent./sal. | Coste $ | stop_reason |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var r in informe.Rutas)
        {
            string Nivel(NivelCaso nivel) => r.PuntuacionPorNivel.TryGetValue(nivel, out var p) ? p.ToString("0.00", inv) : "—";
            md.AppendLine(inv,
                $"| {r.Ruta} | {r.Casos} | {r.PuntuacionMedia:0.00} | {r.CasosPerfectos}/{r.Casos} | {Nivel(NivelCaso.Sencillo)} | {Nivel(NivelCaso.Medio)} | {Nivel(NivelCaso.Dificil)} | {Nivel(NivelCaso.Adversarial)} | {r.LatenciaMedianaMs} ms | {r.LatenciaMaximaMs} ms | {r.TokensEntrada}/{r.TokensSalida} | {(r.CosteUsd is { } c ? c.ToString("0.0000", inv) : "—")} | {string.Join(", ", r.StopReasons.Select(s => $"{s.Key}×{s.Value}"))} |");
        }

        var conFallos = informe.Casos.Where(c => c.Puntuacion < 1).ToList();
        if (conFallos.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("| Caso con fallos | Puntuación | Qué falló |");
            md.AppendLine("|---|---:|---|");
            foreach (var c in conFallos)
            {
                var motivo = c.ErrorServicio is not null ? $"el servicio falló: {c.ErrorServicio} (stop_reason {c.StopReason ?? "—"})" : string.Join("; ", c.Fallos);
                md.AppendLine(inv, $"| {c.Caso} | {c.Puntuacion:0.00} | {motivo.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ")} |");
            }
        }

        return md.ToString();
    }
}
