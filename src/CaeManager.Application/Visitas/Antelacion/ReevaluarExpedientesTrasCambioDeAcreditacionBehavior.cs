using CaeManager.Application.Documentos;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Visitas.Antelacion;

/// <summary>
/// Tras una orden <see cref="ICambiaEstadoDeAcreditacion"/> que terminó con éxito, reevalúa los expedientes de Visita
/// afectados por el Documento de la Acreditación (<see cref="IEvaluadorExpedienteVisitaService.EvaluarPorDocumentoAsync"/>),
/// el mismo camino que <c>DocumentacionCambiadaEvent</c>. Va dentro de la serialización de acceso a datos y por fuera
/// del resto: corre cuando la orden ya guardó, y una orden fallida no reevalúa nada. Traga sus propios errores, como el
/// manejador del evento: el cambio de una Acreditación no debe fallar porque no se pudiera sellar una Visita. El
/// Documento se busca con el contexto de la propia orden, bajo la RLS del Tenant propietario.
/// </summary>
public class ReevaluarExpedientesTrasCambioDeAcreditacionBehavior<TRequest, TResponse>(
    IDocumentosQueryContext documentosContext,
    IEvaluadorExpedienteVisitaService evaluador,
    ILogger<ReevaluarExpedientesTrasCambioDeAcreditacionBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var respuesta = await next(cancellationToken);

        if (request is not ICambiaEstadoDeAcreditacion cambio || respuesta is not Result { EsExitoso: true })
            return respuesta;

        try
        {
            var documentoId = await documentosContext.AcreditacionesDocumentoPlataforma
                .Where(a => a.Id == cambio.AcreditacionId)
                .Select(a => (Guid?)a.DocumentoId)
                .FirstOrDefaultAsync(cancellationToken);

            if (documentoId is { } id)
                await evaluador.EvaluarPorDocumentoAsync(id, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "No se pudieron evaluar los expedientes de visita afectados por la acreditación {AcreditacionId}.", cambio.AcreditacionId);
        }

        return respuesta;
    }
}
