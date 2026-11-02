using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Eventos;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Comunicaciones.Eventos;

/// <summary>
/// Deja en el adjunto el Documento que salió de él, tanto en la asignación suelta como en la de lote. Corre después
/// del commit del Documento: si falla, el Documento sigue siendo válido y el adjunto se queda pendiente, con aviso.
/// </summary>
public class MarcarAdjuntoAsignadoHandler(
    IAdjuntoMensajeRepository repositorio, ICurrentUserService currentUserService, IUnitOfWork unitOfWork,
    ILogger<MarcarAdjuntoAsignadoHandler> logger)
    : INotificationHandler<DocumentoActualizadoEvent>
{
    public async Task Handle(DocumentoActualizadoEvent notification, CancellationToken cancellationToken)
    {
        try
        {
            var adjunto = await repositorio.ObtenerPorIdAsync(notification.AdjuntoId, cancellationToken);
            if (adjunto is null || !adjunto.EstaPendiente)
            {
                logger.LogWarning("El adjunto {AdjuntoId} no estaba pendiente al asignarse al documento {DocumentoId}.", notification.AdjuntoId, notification.DocumentoId);
                return;
            }

            adjunto.MarcarAsignado(notification.DocumentoId, await currentUserService.ObtenerUsuarioActualIdAsync(), DateTime.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "No se pudo marcar como asignado el adjunto {AdjuntoId}.", notification.AdjuntoId);
        }
    }
}
