using CaeManager.Application.Common;
using CaeManager.Application.Reportes.Queries;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Exportacion;
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
///
/// Cada descarga, Excel o PDF, deja su rastro antes de entregarse
/// (<see cref="IRegistroExportacionService"/>; ASVS 4.0.3 V7.1.3 y V7.2.2).
/// </summary>
public static class ReportesEndpoints
{
    private const string TipoExcel = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly string[] RolesConAccesoAReportes =
        [Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae, Roles.GestorCae, Roles.Consulta];

    public static IEndpointRouteBuilder MapReportesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var grupo = endpoints.MapGroup("/reportes")
            .RequireAuthorization(politica => politica.RequireRole(RolesConAccesoAReportes));

        grupo.MapGet("/vigencia.xlsx", VigenciaXlsxAsync);
        grupo.MapGet("/vigencia.pdf", VigenciaPdfAsync);
        grupo.MapGet("/asignaciones.xlsx", AsignacionesXlsxAsync);
        grupo.MapGet("/asignaciones.pdf", AsignacionesPdfAsync);

        return endpoints;
    }

    public static async Task<IResult> VigenciaXlsxAsync(
        IMediator mediator, [Microsoft.AspNetCore.Mvc.FromServices] IRegistroExportacionService registroExportacion, Guid? clienteId, Guid? centroId, bool incluirVigentes, CancellationToken cancellationToken)
    {
        var consulta = new GenerarInformeVigenciaQuery(clienteId, centroId, incluirVigentes);
        var informe = await mediator.Send(consulta, cancellationToken);
        if (informe is null)
            return Results.NotFound();

        var bytes = ConstructorInformeArchivos.ExcelVigencia(informe);
        await registroExportacion.RegistrarAsync(
            nameof(Documento), informe.Filas.Count, CriteriosVigencia("xlsx", consulta), cancellationToken);
        return Results.File(bytes, TipoExcel, "informe.xlsx");
    }

    public static async Task<IResult> VigenciaPdfAsync(
        IMediator mediator, [Microsoft.AspNetCore.Mvc.FromServices] IRegistroExportacionService registroExportacion, Guid? clienteId, Guid? centroId, bool incluirVigentes, CancellationToken cancellationToken)
    {
        var consulta = new GenerarInformeVigenciaQuery(clienteId, centroId, incluirVigentes);
        var informe = await mediator.Send(consulta, cancellationToken);
        if (informe is null)
            return Results.NotFound();

        var bytes = ConstructorInformeArchivos.PdfVigencia(informe, incluirVigentes);
        await registroExportacion.RegistrarAsync(
            nameof(Documento), informe.Filas.Count, CriteriosVigencia("pdf", consulta), cancellationToken);
        return Results.File(bytes, "application/pdf", "informe.pdf");
    }

    public static async Task<IResult> AsignacionesXlsxAsync(
        IMediator mediator, [Microsoft.AspNetCore.Mvc.FromServices] IRegistroExportacionService registroExportacion, Guid? clienteId, Guid? centroId, CancellationToken cancellationToken)
    {
        var consulta = new GenerarInformeAsignacionesQuery(clienteId, centroId);
        var informe = await mediator.Send(consulta, cancellationToken);
        if (informe is null)
            return Results.NotFound();

        var bytes = ConstructorInformeArchivos.ExcelAsignaciones(informe);
        await registroExportacion.RegistrarAsync(
            nameof(Asignacion), informe.Filas.Count, CriteriosAsignaciones("xlsx", consulta), cancellationToken);
        return Results.File(bytes, TipoExcel, "informe.xlsx");
    }

    public static async Task<IResult> AsignacionesPdfAsync(
        IMediator mediator, [Microsoft.AspNetCore.Mvc.FromServices] IRegistroExportacionService registroExportacion, Guid? clienteId, Guid? centroId, CancellationToken cancellationToken)
    {
        var consulta = new GenerarInformeAsignacionesQuery(clienteId, centroId);
        var informe = await mediator.Send(consulta, cancellationToken);
        if (informe is null)
            return Results.NotFound();

        var bytes = ConstructorInformeArchivos.PdfAsignaciones(informe);
        await registroExportacion.RegistrarAsync(
            nameof(Asignacion), informe.Filas.Count, CriteriosAsignaciones("pdf", consulta), cancellationToken);
        return Results.File(bytes, "application/pdf", "informe.pdf");
    }

    // Con centro, las consultas ignoran el Cliente empresarial: el rastro dice el filtro que se aplicó.
    private static IReadOnlyDictionary<string, string> CriteriosVigencia(string formato, GenerarInformeVigenciaQuery consulta)
    {
        var (cliente, centro, incluirVigentes) = consulta;
        return CriteriosExportacion.Desde(
            ("listado", "reporteVigencia"),
            ("formato", formato),
            ("cliente", centro is null ? cliente?.ToString() : null),
            ("centro", centro?.ToString()),
            ("incluirVigentes", incluirVigentes ? "true" : null));
    }

    private static IReadOnlyDictionary<string, string> CriteriosAsignaciones(string formato, GenerarInformeAsignacionesQuery consulta)
    {
        var (cliente, centro) = consulta;
        return CriteriosExportacion.Desde(
            ("listado", "reporteAsignaciones"),
            ("formato", formato),
            ("cliente", centro is null ? cliente?.ToString() : null),
            ("centro", centro?.ToString()));
    }
}
