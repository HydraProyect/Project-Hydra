using CaeManager.Application.Common;
using CaeManager.Application.Reclamaciones.Eventos;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Comunicaciones.Eventos;

/// <summary>
/// Traduce el envío de un lote de reclamación documental en una entrada del
/// Unified Timeline de la Conversacion por la que salió
/// (docs/COMUNICACIONES.md § 12.3/§ 16.7). Falla sola, sin deshacer la
/// reclamación ya registrada — mismo criterio de "mejor esfuerzo tras el commit
/// principal" que <see cref="RegistrarEventoVisitaCreadaHandler"/>: el correo ya
/// salió y el historial ya está escrito, así que perder la entrada del timeline
/// no puede tumbar la operación.
/// </summary>
public class RegistrarEventoReclamacionEnviadaHandler(
    IEventoConversacionRepository repositorio, IUnitOfWork unitOfWork, ILogger<RegistrarEventoReclamacionEnviadaHandler> logger)
    : INotificationHandler<ReclamacionEnviadaEvent>
{
    public async Task Handle(ReclamacionEnviadaEvent notification, CancellationToken cancellationToken)
    {
        try
        {
            var evento = new EventoConversacion(
                notification.ConversacionId, TipoEventoConversacion.ReclamacionEnviada, notification.ReclamacionId, DateTime.UtcNow);
            repositorio.Agregar(evento);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "No se pudo registrar el evento de reclamación enviada en la conversación {ConversacionId}.", notification.ConversacionId);
        }
    }
}
