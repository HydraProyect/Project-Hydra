using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Integraciones;

public record TokensGraphDto(string AccessToken, string RefreshToken);

public record SuscripcionGraphDto(string GraphSubscriptionId, DateTime FechaExpiracionUtc);

public record ParticipanteGraphDto(string Email, RolParticipante Rol);

public record MensajeGraphDto(
    string MensajeExternoId, string HiloExternoId, string Asunto, string RemitenteEmail, string CuerpoHtml, DateTime FechaUtc,
    IReadOnlyList<ParticipanteGraphDto> Participantes);

/// <summary>
/// El "proveedor de integración" de este slice (ver ARQUITECTURA-INTEGRACIONES.md
/// § 4) — nombrado por el proveedor concreto en vez de una abstracción
/// multi-proveedor genérica: con un único proveedor real (Microsoft 365) esa
/// generalidad es especulativa (YAGNI). Se generaliza a
/// <c>IIntegrationProvider</c> cuando exista un segundo proveedor confirmado.
///
/// La implementación real (Infrastructure) es la única que conoce el
/// formato de wire de Graph — <see cref="ExtraerMensajeIdsDeNotificacion"/>
/// incluido, aunque no haga ninguna llamada HTTP: el mapeo de campos de un
/// proveedor externo vive solo en su adaptador (docs/INTEGRATION_GUIDELINES.md
/// paso 6), nunca en Application.
/// </summary>
public interface IMicrosoft365GraphClient
{
    string ConstruirUrlAutorizacion(string redirectUri, string state);

    Task<Result<TokensGraphDto>> IntercambiarCodigoPorTokensAsync(string code, string redirectUri, CancellationToken cancellationToken);

    /// <summary>Graph puede rotar el refresh token en cada canje — el llamador debe persistir el que devuelve esta llamada, no reutilizar el anterior.</summary>
    Task<Result<TokensGraphDto>> RefrescarTokensAsync(string refreshToken, CancellationToken cancellationToken);

    Task<Result<string>> ObtenerBuzonEmailAsync(string accessToken, CancellationToken cancellationToken);

    /// <summary>Usa el endpoint /reply de Graph — preserva threading automáticamente, nunca reconstruye In-Reply-To/References a mano.</summary>
    Task<Result> EnviarRespuestaAsync(
        string accessToken, string buzonEmail, string mensajeExternoIdOrigen, string cuerpoHtml, CancellationToken cancellationToken);

    Task<Result<MensajeGraphDto>> ObtenerMensajeAsync(
        string accessToken, string buzonEmail, string mensajeId, CancellationToken cancellationToken);

    Task<Result<SuscripcionGraphDto>> CrearSuscripcionAsync(
        string accessToken, string buzonEmail, string notificationUrl, string clientState, CancellationToken cancellationToken);

    Task<Result<SuscripcionGraphDto>> RenovarSuscripcionAsync(string accessToken, string graphSubscriptionId, CancellationToken cancellationToken);

    /// <summary>Best-effort — el llamador no debe bloquear una desconexión local si esto falla (la suscripción expira sola).</summary>
    Task<Result> EliminarSuscripcionAsync(string accessToken, string graphSubscriptionId, CancellationToken cancellationToken);

    /// <summary>Puro parseo del payload de notificación de Graph — sin llamada de red. Devuelve los Ids de mensaje a los que hay que ir a buscar el contenido real.</summary>
    IReadOnlyList<string> ExtraerMensajeIdsDeNotificacion(string payloadJson);

    /// <summary>
    /// Puro parseo, sin llamada de red — el <c>clientState</c> que Graph
    /// devuelve sin cambios en cada notificación (docs/MULTITENANCY.md § 8,
    /// tercer modo). El endpoint del webhook lo compara contra el secreto
    /// guardado en <c>SuscripcionWebhook</c> antes de confiar en nada más
    /// del payload. Null si el payload no trae ninguna notificación válida.
    /// </summary>
    string? ExtraerClientStateDeNotificacion(string payloadJson);
}
