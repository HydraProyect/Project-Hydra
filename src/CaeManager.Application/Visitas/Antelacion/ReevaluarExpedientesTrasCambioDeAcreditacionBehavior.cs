using CaeManager.Application.Documentos;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Visitas.Antelacion;

/// <summary>
/// Tras una orden <see cref="ICambiaEstadoDeAcreditacion"/> que terminó con éxito, reevalúa los expedientes de Visita
/// afectados por el Documento de la Acreditación (<see cref="IEvaluadorExpedienteVisitaService.EvaluarPorDocumentoAsync"/>),
/// el mismo camino que <c>DocumentacionCambiadaEvent</c>. Va dentro de la serialización de acceso a datos y por fuera
/// del resto: corre cuando la orden ya guardó, y una orden fallida no reevalúa nada. Traga sus propios errores, también
/// la cancelación, como el manejador del evento: la orden ya está guardada y no debe devolver un error porque no se
/// pudiera sellar una Visita. El Documento se busca con el contexto de la propia orden, bajo la RLS del Tenant propietario.
///
/// MediatR construye todos los behaviors en cada envío, también para consultas y órdenes sin el marcador: las
/// dependencias se resuelven solo cuando hacen falta, como en <c>RestauracionCambiosDeComandoBehavior</c>, para no exigir
/// la persistencia de Visitas a quien no la usa.
/// </summary>
public class ReevaluarExpedientesTrasCambioDeAcreditacionBehavior<TRequest, TResponse>(
    IServiceProvider servicios,
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
            var documentosContext = servicios.GetRequiredService<IDocumentosQueryContext>();
            var documentoId = await documentosContext.AcreditacionesDocumentoPlataforma
                .Where(a => a.Id == cambio.AcreditacionId)
                .Select(a => (Guid?)a.DocumentoId)
                .FirstOrDefaultAsync(cancellationToken);

            if (documentoId is { } id)
                await servicios.GetRequiredService<IEvaluadorExpedienteVisitaService>().EvaluarPorDocumentoAsync(id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudieron evaluar los expedientes de visita afectados por la acreditación {AcreditacionId}.", cambio.AcreditacionId);
        }

        return respuesta;
    }
}
