namespace CaeManager.Application.Visitas.PaqueteDocumental;

/// <summary>
/// Genera un zip con la documentación vigente de la Empresa y de los
/// Trabajadores de una Visita, y lo deja adjunto en un mensaje saliente
/// automático dentro de la conversación de correo que originó la solicitud
/// — cierra "cuando se programe una visita que no es por plataforma sino
/// por correo se cree un ticket que adjunte automáticamente en un zip los
/// documentos" del pedido del usuario. El "ticket" es el propio hilo de
/// <c>ConversacionCorreo</c>, ya el registro auditable de la gestión — no se
/// introduce una entidad Ticket nueva para esto (YAGNI): <c>Visita.Origen</c>
/// ya deja constancia de que la visita nació de un correo, y este mensaje
/// automático es la respuesta operativa dentro de ese mismo hilo.
/// </summary>
public interface IPaqueteDocumentalVisitaService
{
    Task GenerarYEnviarAsync(Guid visitaId, Guid conversacionCorreoId, CancellationToken cancellationToken = default);
}
