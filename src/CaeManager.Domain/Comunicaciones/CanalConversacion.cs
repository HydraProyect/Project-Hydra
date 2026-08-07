namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Canal por el que transcurre una conversación de la bandeja compartida.
/// La bandeja es multicanal sobre el mismo agregado (decisión 2026-08-04;
/// los antiguos nombres ConversacionCorreo/MensajeCorreo se renombraron a
/// Conversacion/Mensaje en el paso 0 del rediseño Communication Workspace,
/// ver docs/COMUNICACIONES.md § 16.2).
/// </summary>
public enum CanalConversacion
{
    Correo = 0,
    WhatsApp = 1
}
