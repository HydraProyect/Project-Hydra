using CaeManager.Application.Reportes.Queries;
using CaeManager.Infrastructure.Identity;
using MediatR;

namespace CaeManager.Web.Reportes;

/// <summary>
/// Descarga de los informes de la Biblioteca de Reportes. Dos capas de
/// autorización, ninguna prescindible:
///
/// 1. El grupo exige los MISMOS roles que la página /reportes
///    (Reportes.razor) — con solo el FallbackPolicy global heredado,
///    cualquier usuario autenticado (incluido el rol Cliente, que ni
///    siquiera ve la pantalla) podía descargar estos archivos.
/// 2. El alcance de cartera lo comprueba cada Query
///    (GenerarInformeVigenciaQuery/GenerarInformeAsignacionesQuery), porque
///    clienteId/centroId llegan por query string y un rol permitido no
///    implica poder pedir cualquier Guid: fuera de cartera devuelven null y
///    aquí se traduce a 404, nunca a 403 — un 403 confirmaría que el Cliente
///    pedido existe.
/// </summary>
public static class ReportesEndpoints
{
    private static readonly string[] RolesConAccesoAReportes =
        [Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae, Roles.GestorCae, Roles.Consulta];

    public static IEndpointRouteBuilder MapReportesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var grupo = endpoints.MapGroup("/reportes")
            .RequireAuthorization(politica => politica.RequireRole(RolesConAccesoAReportes));

        grupo.MapGet("/vigencia.xlsx", async (
            IMediator mediator, Guid? clienteId, Guid? centroId, bool incluirVigentes, CancellationToken cancellationToken) =>
        {
            var informe = await mediator.Send(new GenerarInformeVigenciaQuery(clienteId, centroId, incluirVigentes), cancellationToken);
            if (informe is null)
                return Results.NotFound();

            var bytes = ConstructorInformeArchivos.ExcelVigencia(informe);
            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "informe.xlsx");
        });

        grupo.MapGet("/vigencia.pdf", async (
            IMediator mediator, Guid? clienteId, Guid? centroId, bool incluirVigentes, CancellationToken cancellationToken) =>
        {
            var informe = await mediator.Send(new GenerarInformeVigenciaQuery(clienteId, centroId, incluirVigentes), cancellationToken);
            if (informe is null)
                return Results.NotFound();

            var bytes = ConstructorInformeArchivos.PdfVigencia(informe, incluirVigentes);
            return Results.File(bytes, "application/pdf", "informe.pdf");
        });

        grupo.MapGet("/asignaciones.xlsx", async (
            IMediator mediator, Guid? clienteId, Guid? centroId, CancellationToken cancellationToken) =>
        {
            var informe = await mediator.Send(new GenerarInformeAsignacionesQuery(clienteId, centroId), cancellationToken);
            if (informe is null)
                return Results.NotFound();

            var bytes = ConstructorInformeArchivos.ExcelAsignaciones(informe);
            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "informe.xlsx");
        });

        grupo.MapGet("/asignaciones.pdf", async (
            IMediator mediator, Guid? clienteId, Guid? centroId, CancellationToken cancellationToken) =>
        {
            var informe = await mediator.Send(new GenerarInformeAsignacionesQuery(clienteId, centroId), cancellationToken);
            if (informe is null)
                return Results.NotFound();

            var bytes = ConstructorInformeArchivos.PdfAsignaciones(informe);
            return Results.File(bytes, "application/pdf", "informe.pdf");
        });

        return endpoints;
    }
}
