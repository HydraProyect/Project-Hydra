using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CaeManager.Application.AsistenteIa.Decisiones;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.AsistenteIa;

/// <summary>
/// Decisiones cerradas del asistente sobre la API de System One de TypeSafe. No
/// hay SDK .NET: es un cliente HTTP propio, como los adaptadores de Anthropic de
/// esta carpeta.
/// <para>
/// Solo transporta. Qué se pregunta, la escotilla de abstención y las defensas
/// contra la inyección las decide <see cref="PlanDecisionCerrada"/> en
/// Application; aquí se serializa ese plan tal cual y se devuelve lo que conteste
/// el proveedor para que el plan lo lea con sus reglas.
/// </para>
/// <para>
/// Inerte salvo que <see cref="TypeSafeOptions.Activo"/> y
/// <see cref="TypeSafeOptions.ApiKey"/> estén puestos (ver allí por qué son dos).
/// </para>
/// </summary>
public class TypeSafeDecisionesCerradasService(
    HttpClient httpClient,
    IOptions<TypeSafeOptions> opciones,
    ILogger<TypeSafeDecisionesCerradasService> logger) : IDecisionesCerradasAsistenteService
{
    /// <summary>
    /// Versión fija, nunca un alias como <c>jev-latest</c>: las mediciones que
    /// justifican los criterios del catálogo se hicieron con esta, y un alias la
    /// cambiaría sin que ningún diff lo mostrara.
    /// </summary>
    public const string Modelo = "jev-1.13.0";

    public const string Endpoint = "https://api.typesafe.ai/v1/systemone";

    public async Task<Result<ClasificacionOrdenDto>> ClasificarOrdenAsync(
        string textoOrden, CancellationToken cancellationToken = default)
    {
        var plan = PlanDecisionCerrada.ParaClasificarOrden(textoOrden);
        if (plan.EsFallido)
            return Result.Fallo<ClasificacionOrdenDto>(plan.Error);

        var respuestas = await PreguntarAsync(plan.Valor, cancellationToken);
        return respuestas.EsFallido
            ? Result.Fallo<ClasificacionOrdenDto>(respuestas.Error)
            : plan.Valor.InterpretarClasificacion(respuestas.Valor);
    }

    public async Task<Result<IReadOnlyList<SeleccionCandidatoDto>>> SeleccionarCandidatosAsync(
        string textoOrden, IReadOnlyList<SeleccionSolicitadaDto> selecciones, CancellationToken cancellationToken = default)
    {
        var plan = PlanDecisionCerrada.ParaSeleccionar(textoOrden, selecciones);
        if (plan.EsFallido)
            return Result.Fallo<IReadOnlyList<SeleccionCandidatoDto>>(plan.Error);

        var respuestas = await PreguntarAsync(plan.Valor, cancellationToken);
        return respuestas.EsFallido
            ? Result.Fallo<IReadOnlyList<SeleccionCandidatoDto>>(respuestas.Error)
            : plan.Valor.InterpretarSelecciones(respuestas.Valor);
    }

    private async Task<Result<IReadOnlyList<RespuestaCerrada>>> PreguntarAsync(
        PlanDecisionCerrada plan, CancellationToken cancellationToken)
    {
        var config = opciones.Value;

        if (!config.Activo || string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return Result.Fallo<IReadOnlyList<RespuestaCerrada>>(Error.Crear(
                "DecisionCerrada.NoConfigurado", "El asistente no está disponible ahora mismo."));
        }

        using var peticion = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(ConstruirSolicitud(plan)),
        };
        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);

            if (!respuesta.IsSuccessStatusCode)
            {
                logger.LogError(
                    "La API de TypeSafe devolvió {StatusCode} al decidir sobre una orden del asistente ({Correlacion}).",
                    (int)respuesta.StatusCode, CorrelacionRespuestaIa.Describir(respuesta));

                return Result.Fallo<IReadOnlyList<RespuestaCerrada>>(Error.Crear(
                    "DecisionCerrada.ErrorApi", "No pudimos analizar la orden automáticamente."));
            }

            var cuerpo = await respuesta.Content.ReadFromJsonAsync<RespuestaSystemOne>(cancellationToken);
            return LeerRespuestas(cuerpo);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al contactar la API de TypeSafe para una orden del asistente.");
            return Result.Fallo<IReadOnlyList<RespuestaCerrada>>(Error.Crear(
                "DecisionCerrada.ErrorRed", "No pudimos analizar la orden automáticamente."));
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "No se pudo deserializar la respuesta de TypeSafe para una orden del asistente.");
            return RespuestaInvalida();
        }
    }

    /// <summary>
    /// <c>criteria</c> es el mapa <c>{clave: descripción}</c>: en una pregunta
    /// <c>choice</c> no existe <c>options</c>, las opciones son sus claves.
    /// </summary>
    internal static SolicitudSystemOne ConstruirSolicitud(PlanDecisionCerrada plan) =>
        new(
            plan.Estado,
            Modelo,
            plan.Preguntas.ToDictionary(
                p => p.Id,
                p => new PreguntaSystemOne(
                    "choice",
                    p.Instrucciones,
                    p.Opciones.ToDictionary(o => o.Clave, o => o.Descripcion, StringComparer.Ordinal)),
                StringComparer.Ordinal));

    /// <summary>
    /// Se exige que conteste el modelo fijado: una respuesta de otra versión
    /// vendría de un modelo que nadie ha medido. La validación de preguntas y
    /// elecciones la hace el plan.
    /// </summary>
    private Result<IReadOnlyList<RespuestaCerrada>> LeerRespuestas(RespuestaSystemOne? cuerpo)
    {
        if (cuerpo?.Answers is null)
            return RespuestaInvalida();

        if (cuerpo.Model != Modelo)
        {
            logger.LogWarning(
                "TypeSafe respondió con el modelo {ModeloRecibido} en vez de {ModeloFijado}; se descarta la respuesta.",
                cuerpo.Model, Modelo);
            return RespuestaInvalida();
        }

        var respuestas = new List<RespuestaCerrada>(cuerpo.Answers.Count);

        foreach (var (preguntaId, respuesta) in cuerpo.Answers)
        {
            if (respuesta is null || respuesta.Type != "choice" || respuesta.Choice is null || respuesta.Confidence is null)
                return RespuestaInvalida();

            respuestas.Add(new RespuestaCerrada(preguntaId, respuesta.Choice, respuesta.Confidence.Value));
        }

        return Result.Exito<IReadOnlyList<RespuestaCerrada>>(respuestas);
    }

    private static Result<IReadOnlyList<RespuestaCerrada>> RespuestaInvalida() =>
        Result.Fallo<IReadOnlyList<RespuestaCerrada>>(Error.Crear(
            "DecisionCerrada.RespuestaInvalida", "No pudimos interpretar el resultado del análisis automático."));

    internal sealed record SolicitudSystemOne(
        [property: JsonPropertyName("state")] IReadOnlyDictionary<string, string> State,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("questions")] IReadOnlyDictionary<string, PreguntaSystemOne> Questions);

    internal sealed record PreguntaSystemOne(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("instructions")] string Instructions,
        [property: JsonPropertyName("criteria")] IReadOnlyDictionary<string, string> Criteria);

    private sealed record RespuestaSystemOne(
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("answers")] Dictionary<string, RespuestaPreguntaSystemOne?>? Answers);

    private sealed record RespuestaPreguntaSystemOne(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("choice")] string? Choice,
        [property: JsonPropertyName("confidence")] double? Confidence);
}
