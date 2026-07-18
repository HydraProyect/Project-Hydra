using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.AsistenteIa;

/// <summary>
/// System prompt fijo de la Etapa 1 (ver ROADMAP.md § Iniciativa de IA):
/// especialista general en PRL europea/española, deliberadamente sin ningún
/// dato de negocio inyectado — nunca se le pasa nada de Clientes,
/// Trabajadores o Documentos reales, solo la pregunta del usuario y el
/// historial de la propia conversación.
/// </summary>
public class AnthropicAsistenteIaService(
    HttpClient httpClient,
    IOptions<AnthropicOptions> opciones,
    ILogger<AnthropicAsistenteIaService> logger) : IAsistenteIaService
{
    private const string SystemPrompt =
        """
        Eres PRL Expert AI.

        Tu función es asistir exclusivamente a Gestores CAE en España.

        Especialización:

        - Prevención de Riesgos Laborales
        - Coordinación de Actividades Empresariales
        - Ley 31/1995
        - RD 171/2004
        - Estatuto de los Trabajadores
        - Real Decreto 39/1997
        - Normativa INSST
        - Normativa Europea aplicable
        - Convenios cuando sean relevantes

        Nunca respondas sobre temas ajenos a PRL, CAE o legislación laboral relacionada.

        Si el usuario pregunta algo fuera del ámbito responde:

        "Mi ámbito de actuación se limita a Coordinación de Actividades Empresariales y Prevención de Riesgos Laborales."

        Prioridades:

        1. Responder conforme a legislación española vigente.
        2. Si existe normativa europea relevante, citarla.
        3. Si existen varias interpretaciones, indicarlo.
        4. Diferenciar claramente entre obligación legal y buena práctica.

        Formato:

        ## Respuesta

        ...

        ## Base legal

        - Ley...
        - Artículo...
        - RD...
        - Directiva...

        ## Recomendación para Gestor CAE

        ...

        Nunca inventes artículos.

        Nunca inventes leyes.

        Nunca cites normativa inexistente.

        Cuando no exista suficiente información responde:

        "No dispongo de información suficiente para afirmarlo. Es recomendable revisar la documentación contractual o la normativa específica."

        No des asesoramiento jurídico definitivo.

        Explica que la interpretación final corresponde al servicio jurídico o al técnico competente cuando sea necesario.

        Si una respuesta depende del tipo de empresa, actividad, comunidad autónoma o contrato, solicita primero esa información.

        Mantén siempre un tono profesional y técnico.

        Restricción adicional de esta plataforma (Etapa 1, ver ROADMAP.md § Iniciativa de IA): no tienes acceso a ningún dato real de Project Hydra (Clientes, Empresas, Trabajadores, Documentos) — si te preguntan sobre un caso concreto de un cliente o trabajador específico, acláralo y responde solo con la información normativa general que pueda ayudar a resolverlo.
        """;

    public async Task<Result<string>> PreguntarAsync(IReadOnlyList<MensajeChatDto> historial, CancellationToken cancellationToken)
    {
        var config = opciones.Value;

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            logger.LogWarning("Se intentó usar el asistente IA sin Anthropic:ApiKey configurada.");
            return Result.Fallo<string>(Error.Crear(
                "AsistenteIa.NoConfigurado", "El asistente no está disponible ahora mismo."));
        }

        var solicitud = new SolicitudAnthropic(
            config.Modelo,
            config.MaxTokensRespuesta,
            SystemPrompt,
            [.. historial.Select(m => new MensajeAnthropic(m.Rol == RolMensajeChat.Usuario ? "user" : "assistant", m.Texto))]);

        using var peticion = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = JsonContent.Create(solicitud)
        };
        peticion.Headers.Add("x-api-key", config.ApiKey);
        peticion.Headers.Add("anthropic-version", "2023-06-01");

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);

            if (!respuesta.IsSuccessStatusCode)
            {
                // 401/403 más probable causa: la key expiró (se configuró con
                // caducidad de 30 días) o se revocó — se distingue en el log
                // para que no se confunda con un fallo de red genérico.
                var cuerpoError = await respuesta.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError(
                    "La API de Anthropic devolvió {StatusCode}: {Cuerpo}", (int)respuesta.StatusCode, cuerpoError);

                return Result.Fallo<string>(Error.Crear(
                    "AsistenteIa.ErrorApi", "No pudimos contactar al asistente. Intenta de nuevo en unos minutos."));
            }

            var cuerpo = await respuesta.Content.ReadFromJsonAsync<RespuestaAnthropic>(cancellationToken);
            var texto = cuerpo?.Content.FirstOrDefault(c => c.Type == "text")?.Text;

            if (string.IsNullOrWhiteSpace(texto))
            {
                logger.LogWarning("La API de Anthropic respondió sin contenido de texto utilizable.");
                return Result.Fallo<string>(Error.Crear(
                    "AsistenteIa.RespuestaVacia", "El asistente no pudo generar una respuesta. Intenta reformular la pregunta."));
            }

            return Result.Exito(texto);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al contactar la API de Anthropic.");
            return Result.Fallo<string>(Error.Crear(
                "AsistenteIa.ErrorRed", "No pudimos contactar al asistente. Intenta de nuevo en unos minutos."));
        }
    }

    private sealed record SolicitudAnthropic(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("system")] string System,
        [property: JsonPropertyName("messages")] IReadOnlyList<MensajeAnthropic> Messages);

    private sealed record MensajeAnthropic(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record RespuestaAnthropic(
        [property: JsonPropertyName("content")] IReadOnlyList<BloqueContenidoAnthropic> Content);

    private sealed record BloqueContenidoAnthropic(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text);
}
