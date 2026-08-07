namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Estado de entrega que reporta WhatsApp Cloud API para los mensajes
/// salientes (webhook statuses[]). Solo aplica a mensajes salientes del canal
/// WhatsApp — null en correo y en entrantes. El orden numérico es la
/// progresión válida (ver Mensaje.ActualizarEstadoEntrega): los
/// webhooks de Meta pueden llegar desordenados y un "delivered" tardío no
/// debe pisar un "read" ya registrado.
/// </summary>
public enum EstadoEntregaMensaje
{
    Enviado = 0,
    Entregado = 1,
    Leido = 2,
    Fallido = 3
}
