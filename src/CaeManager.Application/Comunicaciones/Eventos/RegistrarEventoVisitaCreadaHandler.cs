using CaeManager.Application.Common;
using CaeManager.Application.Visitas.Eventos;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Comunicaciones.Eventos;

/// <summary>
/// Traduce un hecho de Visitas en una entrada del Unified Timeline de la
/// Conversacion que lo originó (docs/COMUNICACIONES.md § 12.3/§ 16.7).
/// Falla sola, sin deshacer la Visita ya creada — mismo criterio de "mejor
/// esfuerzo tras el commit principal" que ya usa CrearVisitaCommandHandler
/// para el paquete documental automático.
/// </summary>
public class RegistrarEventoVisitaCreadaHandler(
    IEventoConversacionRepository repositorio, IUnitOfWork unitOfWork, ILogger<RegistrarEventoVisitaCreadaHandler> logger)
    : INotificationHandler<VisitaCreadaEvent>
{
    public async Task Handle(VisitaCreadaEvent notification, CancellationToken cancellationToken)
    {
        try
        {
            var evento = new EventoConversacion(
                notification.ConversacionId, TipoEventoConversacion.VisitaCreada, notification.VisitaId, DateTime.UtcNow);
            repositorio.Agregar(evento);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo registrar el evento de Visita creada en la conversación {ConversacionId}.", notification.ConversacionId);
        }
    }
}
