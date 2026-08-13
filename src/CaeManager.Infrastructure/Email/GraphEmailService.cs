using CaeManager.Application.Common;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Email;

/// <summary>
/// Envía correo con Microsoft Graph usando permisos de aplicación (flujo
/// client credentials — no hay ningún usuario con sesión iniciada en este
/// contexto, a diferencia del login SSO). Pide un token nuevo en cada envío
/// en vez de cachearlo: el volumen esperado (uso interno, notificaciones
/// puntuales de asignación de rol) no lo justifica — ver PROJECT.md sobre
/// no construir para un volumen que no existe.
/// </summary>
public class GraphEmailService(
    HttpClient httpClient,
    IOptions<GraphEmailOptions> opciones,
    ILogger<GraphEmailService> logger) : IEmailService
{
    public async Task<Result> EnviarAsync(string destinatarioEmail, string asunto, string cuerpoHtml, CancellationToken cancellationToken = default)
    {
        var config = opciones.Value;

        if (!config.EstaConfigurado)
        {
            logger.LogWarning("Se intentó enviar un correo sin Graph:* configurado (destinatario: {Destinatario}).", destinatarioEmail);
            return Result.Fallo(Error.Crear("Email.NoConfigurado", "El envío de correo no está configurado."));
        }

        var token = await ObtenerTokenAsync(config, cancellationToken);
        if (token is null)
            return Result.Fallo(Error.Crear("Email.ErrorAutenticacion", "No pudimos autenticar con Microsoft Graph."));

        var solicitudEnvio = new SolicitudEnvioGraph(new MensajeGraph(
            asunto,
            new CuerpoGraph("HTML", cuerpoHtml),
            [new DestinatarioGraph(new DireccionGraph(destinatarioEmail))]));

        using var peticion = new HttpRequestMessage(
            HttpMethod.Post, $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(config.BuzonRemitente!)}/sendMail")
        {
            Content = JsonContent.Create(solicitudEnvio)
        };
        peticion.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);
            if (respuesta.IsSuccessStatusCode)
                return Result.Exito();

            var cuerpoError = await respuesta.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "Microsoft Graph devolvió {StatusCode} al enviar correo a {Destinatario}: {Cuerpo}",
                (int)respuesta.StatusCode, destinatarioEmail, cuerpoError);
            return Result.Fallo(Error.Crear("Email.ErrorApi", "No pudimos enviar el correo."));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al enviar correo a {Destinatario} vía Microsoft Graph.", destinatarioEmail);
            return Result.Fallo(Error.Crear("Email.ErrorRed", "No pudimos enviar el correo."));
        }
    }

    private async Task<string?> ObtenerTokenAsync(GraphEmailOptions config, CancellationToken cancellationToken)
    {
        using var peticionToken = new HttpRequestMessage(
            HttpMethod.Post, $"https://login.microsoftonline.com/{config.TenantId}/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = config.ClientId!,
                ["client_secret"] = config.ClientSecret!,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials",
            })
        };

        try
        {
            using var respuestaToken = await httpClient.SendAsync(peticionToken, cancellationToken);
            if (!respuestaToken.IsSuccessStatusCode)
            {
                // 401/invalid_client más probable causa: el Client Secret expiró
                // o se revocó (caducan como máximo a los 24 meses, ver DEPLOY.md).
                var cuerpoError = await respuestaToken.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("No se pudo obtener token de Microsoft Graph: {StatusCode} {Cuerpo}", (int)respuestaToken.StatusCode, cuerpoError);
                return null;
            }

            var cuerpo = await respuestaToken.Content.ReadFromJsonAsync<RespuestaTokenGraph>(cancellationToken);
            return cuerpo?.AccessToken;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al pedir token a Microsoft Entra ID.");
            return null;
        }
    }

    private sealed record RespuestaTokenGraph([property: JsonPropertyName("access_token")] string? AccessToken);

    private sealed record SolicitudEnvioGraph(
        [property: JsonPropertyName("message")] MensajeGraph Message,
        [property: JsonPropertyName("saveToSentItems")] bool SaveToSentItems = false);

    private sealed record MensajeGraph(
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("body")] CuerpoGraph Body,
        [property: JsonPropertyName("toRecipients")] IReadOnlyList<DestinatarioGraph> ToRecipients);

    private sealed record CuerpoGraph(
        [property: JsonPropertyName("contentType")] string ContentType,
        [property: JsonPropertyName("content")] string Content);

    private sealed record DestinatarioGraph([property: JsonPropertyName("emailAddress")] DireccionGraph EmailAddress);

    private sealed record DireccionGraph([property: JsonPropertyName("address")] string Address);
}
