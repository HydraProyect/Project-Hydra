using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Eventos;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Comunicaciones.Eventos;

/// <summary>
/// Deja en el adjunto el Documento que salió de él, tanto en la asignación suelta como en la de lote. Corre después
/// del commit del Documento: si falla, el Documento sigue siendo válido y el adjunto se queda pendiente, con aviso.
///
/// El adjunto guarda el destino y el tipo con los que se dio el alta, haya o no propuesta previa: lo que queda escrito
/// es a quién fue el Documento. La asignación suelta sustituye la propuesta porque ahí elige una persona. La de lote
/// parte de la propuesta guardada: si otra sesión la cambió entretanto, el Documento salió de la anterior y marcarlo
/// lo atribuiría a un destino que no es el real, así que el adjunto se queda pendiente para que lo revise una persona.
/// </summary>
public class MarcarAdjuntoAsignadoHandler(
    IAdjuntoMensajeRepository repositorio, ICurrentUserService currentUserService, IUnitOfWork unitOfWork,
    ILogger<MarcarAdjuntoAsignadoHandler> logger)
    : INotificationHandler<DocumentoActualizadoEvent>
{
    public async Task Handle(DocumentoActualizadoEvent notification, CancellationToken cancellationToken)
    {
        AdjuntoMensaje? adjunto = null;
        try
        {
            adjunto = await repositorio.ObtenerPorIdAsync(notification.AdjuntoId, cancellationToken);
            if (adjunto is null || !adjunto.EstaPendiente)
            {
                logger.LogWarning("El adjunto {AdjuntoId} no estaba pendiente al asignarse al documento {DocumentoId}.", notification.AdjuntoId, notification.DocumentoId);
                return;
            }

            if (notification.AplicaPropuestaFijada && !EsLaPropuestaVigente(adjunto, notification))
            {
                logger.LogWarning("El adjunto {AdjuntoId} cambió de destino o de tipo mientras se asignaba: el documento {DocumentoId} salió de la propuesta anterior y el adjunto no se marca.", notification.AdjuntoId, notification.DocumentoId);
                return;
            }

            adjunto.FijarDestino(notification.TrabajadorId, notification.EmpresaId, notification.TipoDocumentoId);
            adjunto.MarcarAsignado(notification.DocumentoId, await currentUserService.ObtenerUsuarioActualIdAsync(), DateTime.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sin esto el cambio rechazado seguiría pendiente en el contexto del circuito y saldría en el guardado siguiente.
            if (adjunto is not null) repositorio.OlvidarCambios(adjunto);
            logger.LogWarning("No se pudo marcar como asignado el adjunto {AdjuntoId} ({Excepcion}).", notification.AdjuntoId, ex.GetType().Name);
        }
    }

    private static bool EsLaPropuestaVigente(AdjuntoMensaje adjunto, DocumentoActualizadoEvent alta) =>
        adjunto.DestinoTrabajadorId == alta.TrabajadorId
        && adjunto.DestinoEmpresaId == alta.EmpresaId
        && adjunto.TipoDocumentoPropuestoId == alta.TipoDocumentoId;
}
