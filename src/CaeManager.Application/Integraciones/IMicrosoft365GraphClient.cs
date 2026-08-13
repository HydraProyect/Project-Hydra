using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Integraciones;

public record TokensGraphDto(string AccessToken, string RefreshToken);

public record SuscripcionGraphDto(string GraphSubscriptionId, DateTime FechaExpiracionUtc);

public record ParticipanteGraphDto(string Email, RolParticipante Rol);

public record AdjuntoGraphDto(string AdjuntoExternoId, string NombreArchivo, string TipoContenido, long TamanoBytes);

/// <summary>Entrada para enviar un adjunto — el contenido va inline en base64 (límite de Graph sin sesión de subida, ver <see cref="IMicrosoft365GraphClient"/>).</summary>
public record AdjuntoParaEnviarDto(string NombreArchivo, string TipoContenido, byte[] Contenido);

public record MensajeGraphDto(
    string MensajeExternoId, string HiloExternoId, string Asunto, string Remitente, string CuerpoHtml, DateTime FechaUtc,
    IReadOnlyList<ParticipanteGraphDto> Participantes, IReadOnlyList<AdjuntoGraphDto> Adjuntos);

/// <summary>Fila ligera para el historial de una carpeta — sin cuerpo, para que listar no cargue el HTML completo de cada mensaje.</summary>
public record MensajeResumenGraphDto(
    string MensajeExternoId, string HiloExternoId, string Asunto, string Remitente, DateTime FechaUtc, bool TieneAdjuntos);

/// <summary>Carpeta del buzón (Bandeja de entrada, Enviados, y las subcarpetas propias del cliente) — ver § "estructura de carpetas".</summary>
public record CarpetaGraphDto(
    string CarpetaExternoId, string Nombre, string? CarpetaPadreExternoId, int ElementosNoLeidos, int TotalElementos);

public record MensajeEnviadoGraphDto(string MensajeExternoId, string HiloExternoId);

public static class LimitesAdjuntosCorreo
{
    /// <summary>
    /// Límite real de Microsoft Graph para adjuntos inline en sendMail/reply
    /// sin usar una sesión de subida (<c>createUploadSession</c>) — por
    /// encima de esto Graph rechaza la petición. No se implementa sesión de
    /// subida en este slice (YAGNI): un correo de gestión CAE con más de
    /// 3 MB de adjuntos es el caso raro, no el común. El llamador (Application)
    /// debe validar contra esto antes de invocar
    /// <see cref="IMicrosoft365GraphClient.EnviarRespuestaAsync"/>/<see cref="IMicrosoft365GraphClient.EnviarNuevoMensajeAsync"/>.
    /// </summary>
    public const long TamanoMaximoTotalAdjuntosBytes = 3 * 1024 * 1024;
}

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

    /// <summary>
    /// Usa el endpoint /reply de Graph — preserva threading automáticamente,
    /// nunca reconstruye In-Reply-To/References a mano. <paramref name="adjuntos"/>
    /// va inline en base64 (sin sesión de subida) — respeta
    /// <see cref="LimitesAdjuntosCorreo.TamanoMaximoTotalAdjuntosBytes"/>
    /// del lado del llamador antes de invocar esto.
    /// </summary>
    Task<Result> EnviarRespuestaAsync(
        string accessToken, string buzonEmail, string mensajeExternoIdOrigen, string cuerpoHtml,
        IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos, CancellationToken cancellationToken);

    /// <summary>Redactar un mensaje nuevo (no una respuesta a un hilo existente) — borrador + envío, devuelve los Ids externos (mensaje e hilo).</summary>
    Task<Result<MensajeEnviadoGraphDto>> EnviarNuevoMensajeAsync(
        string accessToken, string buzonEmail, IReadOnlyList<string> destinatarios, string asunto, string cuerpoHtml,
        IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos, CancellationToken cancellationToken);

    /// <summary>Incluye los adjuntos del mensaje (metadatos, no el contenido — ver <see cref="ObtenerContenidoAdjuntoAsync"/>).</summary>
    Task<Result<MensajeGraphDto>> ObtenerMensajeAsync(
        string accessToken, string buzonEmail, string mensajeId, CancellationToken cancellationToken);

    /// <summary>Contenido binario de un adjunto ya listado en <see cref="MensajeGraphDto.Adjuntos"/> — llamada aparte porque Graph no lo trae en el GET del mensaje.</summary>
    Task<Result<byte[]>> ObtenerContenidoAdjuntoAsync(
        string accessToken, string mensajeId, string adjuntoExternoId, CancellationToken cancellationToken);

    /// <summary>
    /// Estructura de carpetas del buzón — dos niveles (carpetas de primer
    /// nivel + sus subcarpetas inmediatas), suficiente para el uso real de
    /// un buzón de gestión CAE (Bandeja de entrada/Enviados/Archivo con
    /// como mucho una subcarpeta propia) sin la complejidad de recorrer un
    /// árbol de profundidad arbitraria.
    /// </summary>
    Task<Result<IReadOnlyList<CarpetaGraphDto>>> ListarCarpetasAsync(string accessToken, CancellationToken cancellationToken);

    /// <summary>Historial de una carpeta (más recientes primero) — para consultar cadenas antiguas sin salir a Outlook.</summary>
    Task<Result<IReadOnlyList<MensajeResumenGraphDto>>> ListarMensajesAsync(
        string accessToken, string carpetaExternoId, int top, int skip, CancellationToken cancellationToken);

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
