using CaeManager.Application.Reportes.Queries;
using MediatR;

namespace CaeManager.Web.Reportes;

public static class ReportesEndpoints
{
    public static IEndpointRouteBuilder MapReportesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/reportes/vigencia.xlsx", async (
            IMediator mediator, Guid? clienteId, Guid? centroId, bool incluirVigentes, CancellationToken cancellationToken) =>
        {
            var informe = await mediator.Send(new GenerarInformeVigenciaQuery(clienteId, centroId, incluirVigentes), cancellationToken);
            var bytes = ConstructorInformeArchivos.ExcelVigencia(informe);
            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "informe.xlsx");
        });

        endpoints.MapGet("/reportes/vigencia.pdf", async (
            IMediator mediator, Guid? clienteId, Guid? centroId, bool incluirVigentes, CancellationToken cancellationToken) =>
        {
            var informe = await mediator.Send(new GenerarInformeVigenciaQuery(clienteId, centroId, incluirVigentes), cancellationToken);
            var bytes = ConstructorInformeArchivos.PdfVigencia(informe, incluirVigentes);
            return Results.File(bytes, "application/pdf", "informe.pdf");
        });

        endpoints.MapGet("/reportes/asignaciones.xlsx", async (
            IMediator mediator, Guid? clienteId, Guid? centroId, CancellationToken cancellationToken) =>
        {
            var informe = await mediator.Send(new GenerarInformeAsignacionesQuery(clienteId, centroId), cancellationToken);
            var bytes = ConstructorInformeArchivos.ExcelAsignaciones(informe);
            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "informe.xlsx");
        });

        endpoints.MapGet("/reportes/asignaciones.pdf", async (
            IMediator mediator, Guid? clienteId, Guid? centroId, CancellationToken cancellationToken) =>
        {
            var informe = await mediator.Send(new GenerarInformeAsignacionesQuery(clienteId, centroId), cancellationToken);
            var bytes = ConstructorInformeArchivos.PdfAsignaciones(informe);
            return Results.File(bytes, "application/pdf", "informe.pdf");
        });

        return endpoints;
    }
}
