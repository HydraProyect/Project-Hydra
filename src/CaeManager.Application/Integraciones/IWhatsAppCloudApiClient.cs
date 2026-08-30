using CaeManager.Domain.Common;

namespace CaeManager.Application.Integraciones;

/// <summary>Un mensaje entrante del webhook de WhatsApp. Telefono viene normalizado a E.164 con prefijo + (Meta manda el wa_id sin él).</summary>
public record MensajeEntranteWhatsAppDto(
    string Wamid,
    string Telefono,
    string? NombrePerfil,
    string Tipo,
    string? Texto,
    string? MediaId,
    string? NombreArchivo,
    string? TipoContenido,
    DateTime FechaUtc);

/// <summary>Un cambio de estado de un mensaje saliente (statuses[]): Estado es el literal de Meta (sent/delivered/read/failed).</summary>
public record EstadoMensajeWhatsAppDto(string Wamid, string Estado, string? Error);

/// <summary>Lo que trae una notificación de webhook para UNA línea (phone_number_id) concreta.</summary>
public record NotificacionWhatsAppDto(
    string PhoneNumberId,
    IReadOnlyList<MensajeEntranteWhatsAppDto> Mensajes,
    IReadOnlyList<EstadoMensajeWhatsAppDto> Estados);

/// <summary>
/// Fragmento del sobre firmado de Meta que corresponde a UNA sola línea —
/// <see cref="PayloadJson"/> contiene solo los <c>entry[].changes[]</c> cuyo
/// <c>value.metadata.phone_number_id</c> es <see cref="PhoneNumberId"/>,
/// nunca los del resto de líneas que Meta pudo agrupar en la misma
/// notificación. Es lo único que <see cref="WebhookWhatsAppEndpoints"/> debe
/// persistir por tenant — persistir el payload completo bajo cada tenant
/// filtraría mensajes de un tenant dentro del <c>PayloadCrudo</c> de otro.
/// </summary>
public record FragmentoWebhookWhatsAppDto(string PhoneNumberId, string PayloadJson);

public record MediaDescargadoWhatsAppDto(byte[] Contenido, string TipoContenido);

public static class LimitesMediaWhatsApp
{
    /// <summary>Mismo tope defensivo que los adjuntos de correo (IngestaWebhookService): un media mayor se descarta sin perder el mensaje.</summary>
    public const long TamanoMaximoBytes = 25 * 1024 * 1024;
}

/// <summary>
/// Adaptador de WhatsApp Cloud API (Meta) — segundo proveedor real de
/// mensajería, nombrado por el proveedor concreto por el mismo criterio
/// documentado en <see cref="IMicrosoft365GraphClient"/>: el framework
/// genérico IIntegrationProvider de ARQUITECTURA-INTEGRACIONES.md § 4-5 está
/// pensado para sincronización documental CAE, no para canales de chat —
/// generalizar ahora sería cumplir la letra y violar el espíritu (YAGNI).
///
/// Solo la implementación (Infrastructure) conoce el formato de wire de
/// Meta — los métodos Extraer* son puro parseo sin llamada de red, para que
/// el endpoint del webhook y la ingesta no toquen JSON de proveedor.
/// </summary>
public interface IWhatsAppCloudApiClient
{
    /// <summary>Puro parseo: los phone_number_id presentes en el payload (una notificación puede agrupar varias líneas de la misma app).</summary>
    IReadOnlyList<string> ExtraerPhoneNumberIds(string payloadJson);

    /// <summary>
    /// Puro parseo: reparte el sobre agrupado de Meta en un
    /// <see cref="FragmentoWebhookWhatsAppDto"/> por cada línea presente —
    /// cada fragmento lleva solo sus propios <c>entry[].changes[]</c>. Es lo
    /// que <see cref="WebhookWhatsAppEndpoints"/> debe persistir por tenant
    /// (nunca <see cref="ExtraerPhoneNumberIds"/> + el payload completo).
    /// </summary>
    IReadOnlyList<FragmentoWebhookWhatsAppDto> ParticionarPorPhoneNumberId(string payloadJson);

    /// <summary>Puro parseo: mensajes entrantes y estados del payload que pertenecen a la línea indicada.</summary>
    NotificacionWhatsAppDto ExtraerNotificacion(string payloadJson, string phoneNumberId);

    /// <summary>
    /// Envía un mensaje de texto libre. Solo válido dentro de la ventana de
    /// servicio de 24 h (la valida el Command llamador contra la
    /// conversación — fuera de ventana Meta lo rechazaría igualmente).
    /// Devuelve el wamid asignado por Meta (messages[0].id), que el llamador
    /// persiste como MensajeExternoId para casar los statuses posteriores.
    /// </summary>
    Task<Result<string>> EnviarTextoAsync(
        string tokenAcceso, string phoneNumberId, string telefonoDestino, string texto, CancellationToken cancellationToken);

    /// <summary>Dos saltos: GET /{mediaId} devuelve una URL efímera (~5 min) que hay que descargar con el mismo Bearer.</summary>
    Task<Result<MediaDescargadoWhatsAppDto>> DescargarMediaAsync(
        string tokenAcceso, string mediaId, CancellationToken cancellationToken);
}
