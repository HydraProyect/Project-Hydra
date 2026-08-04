namespace CaeManager.Domain.Integraciones;

/// <summary>
/// Cómo se asignan las conversaciones nuevas que entran por una línea
/// WhatsApp cuando el teléfono del contacto no identifica ya a un Cliente
/// (leg 2 del enrutamiento híbrido — ver IngestaWebhookWhatsAppService):
/// GestorFijo = todo lo que entra por la línea va a su comercial dueño
/// (modelo outbound); PoolInbound = reparto equitativo por carga entre los
/// MiembrosPool de la línea (modelo inbound).
/// </summary>
public enum ModoAsignacionLinea
{
    GestorFijo = 0,
    PoolInbound = 1
}
