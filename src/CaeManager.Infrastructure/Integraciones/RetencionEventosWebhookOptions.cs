namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// Retención del payload crudo de <see cref="CaeManager.Domain.Integraciones.EventoWebhook"/>
/// (auditoría módulo 6): el JSON entrante de WhatsApp/Graph contiene PHI/PII
/// de conversación (cuerpos, teléfonos, nombres) que solo hace falta
/// mientras el evento puede necesitar reintentarse — una vez en un estado
/// terminal (Completado/DescartadoDefinitivo), ObtenerConversacionPorIdQuery
/// y el resto de la UI ya leen el contenido real desde Mensaje/Conversacion,
/// nunca desde aquí.
///
/// <b>Apagado por defecto</b>, mismo criterio que <c>RetencionDatosOptions.Activa</c>
/// y <c>Backups:Activo</c>: una redacción automática que se activa sola tras
/// un despliegue es la clase de cambio de contenido que no tiene vuelta
/// atrás si el plazo resultara demasiado corto.
/// </summary>
public class RetencionEventosWebhookOptions
{
    public const string SeccionConfiguracion = "RetencionEventosWebhook";

    public bool Activa { get; set; }

    /// <summary>
    /// Días desde la recepción tras los que un evento ya terminado se
    /// redacta. Corto a propósito (el hallazgo de la auditoría pedía "TTL
    /// corto"): con reintentos acotados a <c>EventoWebhook.MaximoIntentos</c>
    /// y backoff máximo de unos minutos, un evento resuelve su estado final
    /// en cuestión de horas como mucho — el resto del plazo es margen para
    /// que soporte pueda investigar un fallo, no una necesidad operativa.
    /// </summary>
    public int DiasRetencion { get; set; } = 7;
}
