using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using CaeManager.Infrastructure.AsistenteIa;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests.BancoModelosIa;

/// <summary>
/// Parámetros de una ejecución del banco. Todo entra por variables de
/// entorno para que comparar un modelo nuevo no exija tocar código:
/// <c>BANCO_IA_MODELO</c>, <c>BANCO_IA_ESFUERZO</c>, <c>BANCO_IA_RUTAS</c>
/// (lista separada por comas de <see cref="RutaIa"/>, vacío = todas),
/// <c>BANCO_IA_SALIDA</c> (carpeta del informe) y, para un modelo que no
/// esté en <see cref="TarifasAnthropic"/>, <c>BANCO_IA_PRECIO_ENTRADA</c> y
/// <c>BANCO_IA_PRECIO_SALIDA</c> en dólares por millón de tokens.
/// </summary>
public sealed record ParametrosBanco(
    string Modelo, string? Esfuerzo, IReadOnlySet<RutaIa>? Rutas, string CarpetaSalida, decimal? PrecioEntrada, decimal? PrecioSalida)
{
    public static ParametrosBanco DesdeEntorno(Func<string, string?> leer)
    {
        static string? Valor(string? crudo) => string.IsNullOrWhiteSpace(crudo) ? null : crudo.Trim();

        var rutas = Valor(leer("BANCO_IA_RUTAS"))?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(nombre => Enum.Parse<RutaIa>(nombre, ignoreCase: true))
            .ToHashSet();

        static decimal? Precio(string? crudo) =>
            crudo is null ? null : decimal.Parse(crudo, System.Globalization.CultureInfo.InvariantCulture);

        return new ParametrosBanco(
            // Sin parámetro, el modelo es el que el producto usa por defecto: la
            // medición de referencia es siempre «lo que hay hoy en producción».
            Valor(leer("BANCO_IA_MODELO")) ?? new AnthropicOptions().Modelo,
            Valor(leer("BANCO_IA_ESFUERZO")),
            rutas,
            Valor(leer("BANCO_IA_SALIDA")) ?? Path.Combine(Path.GetTempPath(), "banco-modelos-ia"),
            Precio(Valor(leer("BANCO_IA_PRECIO_ENTRADA"))),
            Precio(Valor(leer("BANCO_IA_PRECIO_SALIDA"))));
    }
}

/// <summary>Lo que la sonda vio pasar por el cable en la llamada de un caso.</summary>
public sealed record LlamadaObservada(
    string? ModeloEnviado, string? EsfuerzoEnviado, int? MaxTokensEnviado,
    int EstadoHttp, long LatenciaMs, int Intentos,
    int TokensEntrada, int TokensSalida, string? StopReason, string? ModeloQueRespondio);

/// <summary>
/// Sonda entre el servicio del producto y la API. Lee de la PETICIÓN el
/// modelo, el esfuerzo y el tope que el producto envía de verdad (no lo que
/// el banco cree haber configurado) y de la RESPUESTA el uso de tokens, el
/// <c>stop_reason</c> y el modelo que atendió. Los servicios no exponen nada
/// de eso, y así el banco no obliga a cambiarlos.
///
/// Reintenta 429, 5xx y 529 (saturación), hasta tres intentos: un proveedor
/// saturado no es un fallo del modelo que se está midiendo. La latencia que
/// se anota es la del intento que respondió.
/// </summary>
public sealed class SondaAnthropic(HttpMessageHandler interno) : DelegatingHandler(interno)
{
    private const int IntentosMaximos = 3;

    public List<LlamadaObservada> Llamadas { get; } = [];

    public TimeSpan EsperaEntreIntentos { get; init; } = TimeSpan.FromSeconds(8);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? modelo = null, esfuerzo = null;
        int? maxTokens = null;
        if (request.Content is not null)
        {
            using var peticion = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            var raiz = peticion.RootElement;
            modelo = raiz.TryGetProperty("model", out var m) ? m.GetString() : null;
            maxTokens = raiz.TryGetProperty("max_tokens", out var t) ? t.GetInt32() : null;
            esfuerzo = raiz.TryGetProperty("output_config", out var salida) && salida.TryGetProperty("effort", out var e) ? e.GetString() : null;
        }

        for (var intento = 1; ; intento++)
        {
            var reloj = Stopwatch.StartNew();
            var respuesta = await base.SendAsync(request, cancellationToken);
            await respuesta.Content.LoadIntoBufferAsync(cancellationToken);
            reloj.Stop();

            var estado = (int)respuesta.StatusCode;
            var reintentable = respuesta.StatusCode == HttpStatusCode.TooManyRequests || estado >= 500;
            if (reintentable && intento < IntentosMaximos)
            {
                var espera = respuesta.Headers.RetryAfter?.Delta ?? EsperaEntreIntentos * intento;
                respuesta.Dispose();
                await Task.Delay(espera, cancellationToken);
                continue;
            }

            Llamadas.Add(Observar(await respuesta.Content.ReadAsStringAsync(cancellationToken), modelo, esfuerzo, maxTokens, estado, reloj.ElapsedMilliseconds, intento));
            return respuesta;
        }
    }

    private static LlamadaObservada Observar(
        string cuerpo, string? modelo, string? esfuerzo, int? maxTokens, int estado, long latenciaMs, int intentos)
    {
        int entrada = 0, salida = 0;
        string? stop = null, modeloRespuesta = null;
        try
        {
            using var respuesta = JsonDocument.Parse(cuerpo);
            var raiz = respuesta.RootElement;
            if (raiz.TryGetProperty("usage", out var uso))
            {
                entrada = uso.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0;
                salida = uso.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0;
            }

            stop = raiz.TryGetProperty("stop_reason", out var s) ? s.GetString() : null;
            modeloRespuesta = raiz.TryGetProperty("model", out var m) ? m.GetString() : null;
        }
        catch (JsonException)
        {
            // Un cuerpo que no es JSON (una página de error de un proxy) deja los campos vacíos; el estado HTTP ya lo delata.
        }

        return new LlamadaObservada(modelo, esfuerzo, maxTokens, estado, latenciaMs, intentos, entrada, salida, stop, modeloRespuesta);
    }
}

/// <summary>Modelo simulado: responde siempre con el mismo texto, con la forma de la Messages API. Para probar el banco sin clave ni gasto.</summary>
public sealed class ModeloSimulado(string texto, string stopReason = "end_turn") : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var cuerpo = JsonSerializer.Serialize(new
        {
            model = "modelo-simulado",
            stop_reason = stopReason,
            content = new[] { new { type = "text", text = texto } },
            usage = new { input_tokens = 120, output_tokens = 30 },
        });

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(cuerpo, Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>El resultado medido de un caso: calidad, y lo que costó obtenerla.</summary>
public sealed record ResultadoCaso(
    RutaIa Ruta, string Caso, NivelCaso Nivel, string Descripcion,
    double Puntuacion, int ComprobacionesCorrectas, int ComprobacionesTotales, IReadOnlyList<string> Fallos,
    IReadOnlyDictionary<string, double> Medidas, string? ErrorServicio,
    string? ModeloEnviado, string? EsfuerzoEnviado, int? MaxTokensEnviado, string? ModeloQueRespondio,
    int EstadoHttp, long LatenciaMs, int Intentos, int TokensEntrada, int TokensSalida, string? StopReason, decimal? CosteUsd);

public static class EjecutorBancoModelosIa
{
    /// <summary>
    /// Ejecuta los casos contra los servicios reales del producto. El
    /// transporte lo decide <paramref name="transportePorCaso"/>: la red de
    /// verdad en la medición, un <see cref="ModeloSimulado"/> en las pruebas
    /// de sensibilidad. Los casos van en serie: la medición de latencia no se
    /// contamina y no se dispara el límite de peticiones de la clave de CI.
    /// </summary>
    public static async Task<IReadOnlyList<ResultadoCaso>> EjecutarAsync(
        ParametrosBanco parametros, string apiKey, IEnumerable<CasoBanco> casos,
        Func<CasoBanco, HttpMessageHandler> transportePorCaso, CancellationToken cancellationToken = default)
    {
        var opciones = Options.Create(ConstruirOpciones(parametros, apiKey));
        var tarifa = TarifasAnthropic.Para(parametros);
        var resultados = new List<ResultadoCaso>();

        foreach (var caso in casos.Where(c => parametros.Rutas is null || parametros.Rutas.Contains(c.Ruta)))
        {
            using var sonda = new SondaAnthropic(transportePorCaso(caso));
            using var http = new HttpClient(sonda) { Timeout = TimeSpan.FromMinutes(10) };
            var servicios = new ServiciosAnthropic(
                new AnthropicDeteccionRelevanciaCaeService(http, opciones, NullLogger<AnthropicDeteccionRelevanciaCaeService>.Instance),
                new AnthropicDeteccionGestionCorreoService(http, opciones, NullLogger<AnthropicDeteccionGestionCorreoService>.Instance),
                new AnthropicDeteccionVisitaCorreoService(http, opciones, NullLogger<AnthropicDeteccionVisitaCorreoService>.Instance),
                new AnthropicDocumentAIProvider(http, opciones, NullLogger<AnthropicDocumentAIProvider>.Instance),
                new AnthropicExtraccionTrabajadoresIaService(http, opciones, NullLogger<AnthropicExtraccionTrabajadoresIaService>.Instance),
                new AnthropicAsistenteIaService(http, opciones, NullLogger<AnthropicAsistenteIaService>.Instance));

            Evaluacion evaluacion;
            try
            {
                evaluacion = await caso.EjecutarAsync(servicios, cancellationToken);
            }
            catch (Exception ex) when (ex is TaskCanceledException or JsonException or InvalidOperationException)
            {
                // Una excepción que el servicio deja escapar (tiempo agotado, cuerpo de respuesta inesperado) es un
                // resultado medible del caso, no un motivo para perder la medición de los demás.
                evaluacion = Evaluacion.Fallida($"Excepcion.{ex.GetType().Name}");
            }

            var llamada = sonda.Llamadas.LastOrDefault();
            resultados.Add(new ResultadoCaso(
                caso.Ruta, caso.Id, caso.Nivel, caso.Descripcion,
                Math.Round(evaluacion.Puntuacion, 4),
                evaluacion.Comprobaciones.Count(c => c.Correcta), evaluacion.Comprobaciones.Count,
                [.. evaluacion.Comprobaciones.Where(c => !c.Correcta).Select(c => c.Detalle is null ? c.Nombre : $"{c.Nombre}: {c.Detalle}")],
                evaluacion.Medidas, evaluacion.ErrorServicio,
                llamada?.ModeloEnviado, llamada?.EsfuerzoEnviado, llamada?.MaxTokensEnviado, llamada?.ModeloQueRespondio,
                llamada?.EstadoHttp ?? 0, llamada?.LatenciaMs ?? 0, llamada?.Intentos ?? 0,
                llamada?.TokensEntrada ?? 0, llamada?.TokensSalida ?? 0, llamada?.StopReason,
                llamada is null ? null : tarifa?.Coste(llamada.TokensEntrada, llamada.TokensSalida)));
        }

        return resultados;
    }

    /// <summary>
    /// Las opciones se enlazan por configuración, igual que en producción, y
    /// no asignando propiedades: así el esfuerzo llega a <c>AnthropicOptions</c>
    /// en cuanto el producto lo tenga como opción, sin que este fichero
    /// dependa de que la propiedad exista. Si el producto no lo envía, la
    /// sonda lo ve y <see cref="ValidacionDelInstrumento"/> lo declara.
    /// </summary>
    private static AnthropicOptions ConstruirOpciones(ParametrosBanco parametros, string apiKey)
    {
        var valores = new Dictionary<string, string?>
        {
            ["Anthropic:ApiKey"] = apiKey,
            ["Anthropic:Modelo"] = parametros.Modelo,
        };
        if (parametros.Esfuerzo is not null)
        {
            valores["Anthropic:Esfuerzo"] = parametros.Esfuerzo;
            valores["Anthropic:EsfuerzoAsistente"] = parametros.Esfuerzo;
        }

        var opciones = new AnthropicOptions();
        new ConfigurationBuilder().AddEnvironmentVariables().AddInMemoryCollection(valores).Build()
            .GetSection(AnthropicOptions.SeccionConfiguracion).Bind(opciones);
        return opciones;
    }
}

/// <summary>
/// Comprueba que la medición mide lo que dice medir. Devuelve los problemas
/// encontrados; con alguno, el informe se escribe igualmente pero la
/// ejecución termina en rojo, porque sus cifras no son comparables.
/// </summary>
public static class ValidacionDelInstrumento
{
    public static IReadOnlyList<string> Problemas(ParametrosBanco parametros, IReadOnlyList<ResultadoCaso> resultados)
    {
        var problemas = new List<string>();

        if (resultados.Count == 0)
            problemas.Add("No se ejecutó ningún caso: el filtro de rutas no casa con el corpus.");

        var sinLlamada = resultados.Where(r => r.Intentos == 0).Select(r => r.Caso).ToList();
        if (sinLlamada.Count > 0)
            problemas.Add($"Casos que no llegaron a llamar a la API: {string.Join(", ", sinLlamada)}.");

        var conErrorHttp = resultados.Where(r => r.Intentos > 0 && r.EstadoHttp != 200).Select(r => $"{r.Caso} ({r.EstadoHttp})").ToList();
        if (conErrorHttp.Count > 0)
            problemas.Add($"Casos que la API rechazó, sin medición de calidad: {string.Join(", ", conErrorHttp)}.");

        var otroModelo = resultados.Where(r => r.Intentos > 0 && r.ModeloEnviado != parametros.Modelo).Select(r => r.Caso).ToList();
        if (otroModelo.Count > 0)
            problemas.Add($"El producto no envió el modelo pedido ({parametros.Modelo}) en: {string.Join(", ", otroModelo)}.");

        if (parametros.Esfuerzo is not null)
        {
            var otroEsfuerzo = resultados.Where(r => r.Intentos > 0 && r.EsfuerzoEnviado != parametros.Esfuerzo).Select(r => r.Caso).ToList();
            if (otroEsfuerzo.Count > 0)
            {
                problemas.Add(
                    $"Se pidió esfuerzo «{parametros.Esfuerzo}» pero el producto no lo envió en la petición de {otroEsfuerzo.Count} casos " +
                    "(output_config.effort ausente o distinto): esta ejecución NO mide ese esfuerzo.");
            }
        }

        return problemas;
    }
}
