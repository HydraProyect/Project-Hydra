using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// Adaptador concreto de Microsoft 365 (ver ARQUITECTURA-INTEGRACIONES.md §
/// 5/12) — flujo delegado (authorization code + offline_access), no
/// client-credentials como <c>GraphEmailService</c>: aquí no hay "el propio
/// buzón de Hydra", cada <c>ConexionIntegracion</c> es el buzón real de un
/// cliente distinto, consentido por su propio administrador. Por eso todas
/// las llamadas usan <c>/me/...</c> (resuelto por el token, no por un UPN
/// en la URL) en vez de <c>/users/{upn}/...</c>.
/// </summary>
public class Microsoft365GraphClient(
    HttpClient httpClient,
    IOptions<Microsoft365GraphOptions> opciones,
    ILogger<Microsoft365GraphClient> logger) : IMicrosoft365GraphClient
{
    private const string EndpointAutorizacion = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
    private const string EndpointToken = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
    private const string ScopesDelegados = "offline_access https://graph.microsoft.com/Mail.Read https://graph.microsoft.com/Mail.Send";

    // Máximo permitido por Graph para suscripciones sobre mensajes de
    // correo (~2.94 días) — ver RenovacionSuscripcionWebhookHostedService,
    // que renueva bastante antes de que esto expire.
    private static readonly TimeSpan DuracionMaximaSuscripcion = TimeSpan.FromMinutes(4230);

    public string ConstruirUrlAutorizacion(string redirectUri, string state)
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = opciones.Value.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["response_mode"] = "query",
            ["scope"] = ScopesDelegados,
            ["state"] = state,
        };

        return $"{EndpointAutorizacion}?{string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value ?? string.Empty)}"))}";
    }

    public async Task<Result<TokensGraphDto>> IntercambiarCodigoPorTokensAsync(
        string code, string redirectUri, CancellationToken cancellationToken)
    {
        var contenido = new Dictionary<string, string>
        {
            ["client_id"] = opciones.Value.ClientId!,
            ["client_secret"] = opciones.Value.ClientSecret!,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["scope"] = ScopesDelegados,
        };

        return await PedirTokensAsync(contenido, cancellationToken);
    }

    public async Task<Result<TokensGraphDto>> RefrescarTokensAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var contenido = new Dictionary<string, string>
        {
            ["client_id"] = opciones.Value.ClientId!,
            ["client_secret"] = opciones.Value.ClientSecret!,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = ScopesDelegados,
        };

        return await PedirTokensAsync(contenido, cancellationToken);
    }

    private async Task<Result<TokensGraphDto>> PedirTokensAsync(
        Dictionary<string, string> contenido, CancellationToken cancellationToken)
    {
        using var peticion = new HttpRequestMessage(HttpMethod.Post, EndpointToken) { Content = new FormUrlEncodedContent(contenido) };

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
            {
                var cuerpoError = await respuesta.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("Microsoft Entra ID devolvió {StatusCode} al canjear tokens: {Cuerpo}", (int)respuesta.StatusCode, cuerpoError);
                return Result.Fallo<TokensGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorAutenticacion", "No pudimos autenticar con Microsoft."));
            }

            var cuerpo = await respuesta.Content.ReadFromJsonAsync<RespuestaTokenGraph>(cancellationToken);
            if (cuerpo?.AccessToken is null || cuerpo.RefreshToken is null)
                return Result.Fallo<TokensGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorAutenticacion", "No pudimos autenticar con Microsoft."));

            return Result.Exito(new TokensGraphDto(cuerpo.AccessToken, cuerpo.RefreshToken));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al canjear tokens con Microsoft Entra ID.");
            return Result.Fallo<TokensGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
        }
    }

    public async Task<Result<string>> ObtenerBuzonEmailAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var peticion = NuevaPeticionGraph(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me?$select=mail,userPrincipalName", accessToken);

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
                return Result.Fallo<string>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "No pudimos leer el buzón conectado."));

            var perfil = await respuesta.Content.ReadFromJsonAsync<PerfilGraph>(cancellationToken);
            var buzon = perfil?.Mail ?? perfil?.UserPrincipalName;
            return string.IsNullOrWhiteSpace(buzon)
                ? Result.Fallo<string>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "No pudimos leer el buzón conectado."))
                : Result.Exito(buzon);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al leer el perfil de Microsoft Graph.");
            return Result.Fallo<string>(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
        }
    }

    public async Task<Result> EnviarRespuestaAsync(
        string accessToken, string buzonEmail, string mensajeExternoIdOrigen, string cuerpoHtml, CancellationToken cancellationToken)
    {
        // /reply preserva conversationId/threading automáticamente — nunca
        // reconstruir In-Reply-To/References a mano (ver
        // ARQUITECTURA-INTEGRACIONES.md § 12.2).
        using var peticion = NuevaPeticionGraph(
            HttpMethod.Post, $"https://graph.microsoft.com/v1.0/me/messages/{Uri.EscapeDataString(mensajeExternoIdOrigen)}/reply", accessToken);
        peticion.Content = JsonContent.Create(new SolicitudRespuestaGraph(cuerpoHtml));

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);
            if (respuesta.IsSuccessStatusCode)
                return Result.Exito();

            var cuerpoError = await respuesta.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "Microsoft Graph devolvió {StatusCode} al responder al mensaje {MensajeId} del buzón {Buzon}: {Cuerpo}",
                (int)respuesta.StatusCode, mensajeExternoIdOrigen, buzonEmail, cuerpoError);
            return Result.Fallo(Error.Crear("Integraciones.Microsoft365.ErrorEnvio", "No pudimos enviar la respuesta."));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al responder al mensaje {MensajeId} vía Microsoft Graph.", mensajeExternoIdOrigen);
            return Result.Fallo(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
        }
    }

    public async Task<Result<MensajeGraphDto>> ObtenerMensajeAsync(
        string accessToken, string buzonEmail, string mensajeId, CancellationToken cancellationToken)
    {
        var url = $"https://graph.microsoft.com/v1.0/me/messages/{Uri.EscapeDataString(mensajeId)}" +
            "?$select=subject,from,toRecipients,ccRecipients,body,receivedDateTime,conversationId";
        using var peticion = NuevaPeticionGraph(HttpMethod.Get, url, accessToken);

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
                return Result.Fallo<MensajeGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "No pudimos leer el mensaje."));

            var mensaje = await respuesta.Content.ReadFromJsonAsync<MensajeDetalleGraph>(cancellationToken);
            if (mensaje is null)
                return Result.Fallo<MensajeGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorApi", "No pudimos leer el mensaje."));

            var participantes = new List<ParticipanteGraphDto>();
            if (!string.IsNullOrWhiteSpace(mensaje.From?.EmailAddress?.Address))
                participantes.Add(new ParticipanteGraphDto(mensaje.From.EmailAddress.Address, RolParticipante.De));
            participantes.AddRange((mensaje.ToRecipients ?? [])
                .Where(r => !string.IsNullOrWhiteSpace(r.EmailAddress?.Address))
                .Select(r => new ParticipanteGraphDto(r.EmailAddress!.Address!, RolParticipante.Para)));
            participantes.AddRange((mensaje.CcRecipients ?? [])
                .Where(r => !string.IsNullOrWhiteSpace(r.EmailAddress?.Address))
                .Select(r => new ParticipanteGraphDto(r.EmailAddress!.Address!, RolParticipante.Cc)));

            var asunto = string.IsNullOrWhiteSpace(mensaje.Subject) ? "(sin asunto)" : mensaje.Subject;

            return Result.Exito(new MensajeGraphDto(
                MensajeExternoId: mensajeId,
                HiloExternoId: mensaje.ConversationId ?? mensajeId,
                Asunto: asunto,
                RemitenteEmail: mensaje.From?.EmailAddress?.Address ?? "desconocido@microsoft365.local",
                CuerpoHtml: mensaje.Body?.Content ?? string.Empty,
                FechaUtc: mensaje.ReceivedDateTime ?? DateTime.UtcNow,
                Participantes: participantes));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al leer el mensaje {MensajeId} vía Microsoft Graph.", mensajeId);
            return Result.Fallo<MensajeGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
        }
    }

    public async Task<Result<SuscripcionGraphDto>> CrearSuscripcionAsync(
        string accessToken, string buzonEmail, string notificationUrl, string clientState, CancellationToken cancellationToken)
    {
        var expiracion = DateTime.UtcNow.Add(DuracionMaximaSuscripcion);
        var solicitud = new SolicitudSuscripcionGraph(
            "created,updated", notificationUrl, "me/mailFolders('Inbox')/messages", expiracion, clientState);

        using var peticion = NuevaPeticionGraph(HttpMethod.Post, "https://graph.microsoft.com/v1.0/subscriptions", accessToken);
        peticion.Content = JsonContent.Create(solicitud);

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
            {
                var cuerpoError = await respuesta.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError(
                    "Microsoft Graph devolvió {StatusCode} al crear la suscripción del buzón {Buzon}: {Cuerpo}",
                    (int)respuesta.StatusCode, buzonEmail, cuerpoError);
                return Result.Fallo<SuscripcionGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorSuscripcion", "No pudimos activar las notificaciones de este buzón."));
            }

            var suscripcion = await respuesta.Content.ReadFromJsonAsync<SuscripcionDetalleGraph>(cancellationToken);
            if (suscripcion?.Id is null || suscripcion.ExpirationDateTime is null)
                return Result.Fallo<SuscripcionGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorSuscripcion", "No pudimos activar las notificaciones de este buzón."));

            return Result.Exito(new SuscripcionGraphDto(suscripcion.Id, suscripcion.ExpirationDateTime.Value));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al crear la suscripción del buzón {Buzon} vía Microsoft Graph.", buzonEmail);
            return Result.Fallo<SuscripcionGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
        }
    }

    public async Task<Result<SuscripcionGraphDto>> RenovarSuscripcionAsync(
        string accessToken, string graphSubscriptionId, CancellationToken cancellationToken)
    {
        var expiracion = DateTime.UtcNow.Add(DuracionMaximaSuscripcion);
        using var peticion = NuevaPeticionGraph(
            HttpMethod.Patch, $"https://graph.microsoft.com/v1.0/subscriptions/{Uri.EscapeDataString(graphSubscriptionId)}", accessToken);
        peticion.Content = JsonContent.Create(new SolicitudRenovacionGraph(expiracion));

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
            {
                var cuerpoError = await respuesta.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError(
                    "Microsoft Graph devolvió {StatusCode} al renovar la suscripción {SubscriptionId}: {Cuerpo}",
                    (int)respuesta.StatusCode, graphSubscriptionId, cuerpoError);
                return Result.Fallo<SuscripcionGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorSuscripcion", "No pudimos renovar las notificaciones de este buzón."));
            }

            return Result.Exito(new SuscripcionGraphDto(graphSubscriptionId, expiracion));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al renovar la suscripción {SubscriptionId} vía Microsoft Graph.", graphSubscriptionId);
            return Result.Fallo<SuscripcionGraphDto>(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
        }
    }

    public async Task<Result> EliminarSuscripcionAsync(string accessToken, string graphSubscriptionId, CancellationToken cancellationToken)
    {
        using var peticion = NuevaPeticionGraph(
            HttpMethod.Delete, $"https://graph.microsoft.com/v1.0/subscriptions/{Uri.EscapeDataString(graphSubscriptionId)}", accessToken);

        try
        {
            using var respuesta = await httpClient.SendAsync(peticion, cancellationToken);
            if (respuesta.IsSuccessStatusCode || respuesta.StatusCode == System.Net.HttpStatusCode.NotFound)
                return Result.Exito(); // ya no existe también cuenta como "eliminada"

            var cuerpoError = await respuesta.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "Microsoft Graph devolvió {StatusCode} al eliminar la suscripción {SubscriptionId}: {Cuerpo}",
                (int)respuesta.StatusCode, graphSubscriptionId, cuerpoError);
            return Result.Fallo(Error.Crear("Integraciones.Microsoft365.ErrorSuscripcion", "No pudimos eliminar la suscripción en Microsoft."));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Fallo de red al eliminar la suscripción {SubscriptionId} vía Microsoft Graph.", graphSubscriptionId);
            return Result.Fallo(Error.Crear("Integraciones.Microsoft365.ErrorRed", "No pudimos conectar con Microsoft."));
        }
    }

    public IReadOnlyList<string> ExtraerMensajeIdsDeNotificacion(string payloadJson)
    {
        try
        {
            var notificacion = JsonSerializer.Deserialize<NotificacionesGraph>(payloadJson);
            return notificacion?.Value?
                .Where(n => !string.IsNullOrWhiteSpace(n.ResourceData?.Id))
                .Select(n => n.ResourceData!.Id!)
                .ToList() ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Payload de notificación de webhook de Microsoft Graph no es JSON válido.");
            return [];
        }
    }

    public string? ExtraerClientStateDeNotificacion(string payloadJson)
    {
        try
        {
            var notificacion = JsonSerializer.Deserialize<NotificacionesGraph>(payloadJson);
            return notificacion?.Value?.FirstOrDefault()?.ClientState;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Payload de notificación de webhook de Microsoft Graph no es JSON válido.");
            return null;
        }
    }

    private static HttpRequestMessage NuevaPeticionGraph(HttpMethod metodo, string url, string accessToken)
    {
        var peticion = new HttpRequestMessage(metodo, url);
        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return peticion;
    }

    private sealed record RespuestaTokenGraph(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken);

    private sealed record PerfilGraph(
        [property: JsonPropertyName("mail")] string? Mail,
        [property: JsonPropertyName("userPrincipalName")] string? UserPrincipalName);

    private sealed record SolicitudRespuestaGraph([property: JsonPropertyName("comment")] string Comment);

    private sealed record DireccionGraph([property: JsonPropertyName("address")] string? Address);

    private sealed record EmailAddressGraph([property: JsonPropertyName("emailAddress")] DireccionGraph? EmailAddress);

    private sealed record CuerpoMensajeGraph([property: JsonPropertyName("content")] string? Content);

    private sealed record MensajeDetalleGraph(
        [property: JsonPropertyName("subject")] string? Subject,
        [property: JsonPropertyName("from")] EmailAddressGraph? From,
        [property: JsonPropertyName("toRecipients")] IReadOnlyList<EmailAddressGraph>? ToRecipients,
        [property: JsonPropertyName("ccRecipients")] IReadOnlyList<EmailAddressGraph>? CcRecipients,
        [property: JsonPropertyName("body")] CuerpoMensajeGraph? Body,
        [property: JsonPropertyName("receivedDateTime")] DateTime? ReceivedDateTime,
        [property: JsonPropertyName("conversationId")] string? ConversationId);

    private sealed record SolicitudSuscripcionGraph(
        [property: JsonPropertyName("changeType")] string ChangeType,
        [property: JsonPropertyName("notificationUrl")] string NotificationUrl,
        [property: JsonPropertyName("resource")] string Resource,
        [property: JsonPropertyName("expirationDateTime")] DateTime ExpirationDateTime,
        [property: JsonPropertyName("clientState")] string ClientState);

    private sealed record SolicitudRenovacionGraph([property: JsonPropertyName("expirationDateTime")] DateTime ExpirationDateTime);

    private sealed record SuscripcionDetalleGraph(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("expirationDateTime")] DateTime? ExpirationDateTime);

    private sealed record RecursoNotificacionGraph([property: JsonPropertyName("id")] string? Id);

    private sealed record NotificacionGraph(
        [property: JsonPropertyName("resourceData")] RecursoNotificacionGraph? ResourceData,
        [property: JsonPropertyName("clientState")] string? ClientState);

    private sealed record NotificacionesGraph([property: JsonPropertyName("value")] IReadOnlyList<NotificacionGraph>? Value);
}
