namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Canal por el que transcurre una conversación de la bandeja compartida.
/// La bandeja es multicanal sobre el mismo agregado (decisión 2026-08-04):
/// los nombres ConversacionCorreo/MensajeCorreo se mantienen como deuda
/// nominal — renombrarlos es un refactor aparte que no se mezcla con la
/// incorporación de WhatsApp (regla de CLAUDE.md).
/// </summary>
public enum CanalConversacion
{
    Correo = 0,
    WhatsApp = 1
}
