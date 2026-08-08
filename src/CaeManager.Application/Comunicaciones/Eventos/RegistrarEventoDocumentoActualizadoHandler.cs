using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Eventos;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Comunicaciones.Eventos;

/// <summary>
/// Traduce un hecho de Documentos en una entrada del Unified Timeline —
/// mismo patrón que <c>RegistrarEventoVisitaCreadaHandler</c>.
/// </summary>
public class RegistrarEventoDocumentoActualizadoHandler(
    IEventoConversacionRepository repositorio, IUnitOfWork unitOfWork, ILogger<RegistrarEventoDocumentoActualizadoHandler> logger)
    : INotificationHandler<DocumentoActualizadoEvent>
{
    public async Task Handle(DocumentoActualizadoEvent notification, CancellationToken cancellationToken)
    {
        try
        {
            var evento = new EventoConversacion(
                notification.ConversacionId, TipoEventoConversacion.DocumentoActualizado, notification.DocumentoId, DateTime.UtcNow);
            repositorio.Agregar(evento);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo registrar el evento de Documento actualizado en la conversación {ConversacionId}.", notification.ConversacionId);
        }
    }
}
