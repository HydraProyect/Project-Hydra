using CaeManager.Application.Documentos.Eventos;
using CaeManager.Application.Visitas.Antelacion;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Visitas.Eventos;

/// <summary>
/// Puente entre "ha cambiado documentación" y "el expediente de esta visita ya
/// está completo". Traga sus propios errores: el alta de un Documento no debe
/// fallar porque el sellado de una Visita no pudiera calcularse.
/// </summary>
public class EvaluarExpedienteAlCambiarDocumentacionHandler(
    IEvaluadorExpedienteVisitaService evaluador,
    ILogger<EvaluarExpedienteAlCambiarDocumentacionHandler> logger)
    : INotificationHandler<DocumentacionCambiadaEvent>
{
    public async Task Handle(DocumentacionCambiadaEvent notification, CancellationToken cancellationToken)
    {
        try
        {
            await evaluador.EvaluarPorDocumentoAsync(notification.DocumentoId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudieron evaluar los expedientes de visita afectados por el documento {DocumentoId}.", notification.DocumentoId);
        }
    }
}
