using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Importacion;
using MediatR;

namespace CaeManager.Web.Features.Documentos;

/// <summary>
/// Sirve el PDF adjunto de un Documento vía un endpoint autenticado — nunca
/// como archivo estático público, precisamente porque IFileStorageService
/// guarda fuera de wwwroot (ver ARCHITECTURE.md, "Archivos").
/// </summary>
public static class DocumentosEndpoints
{
    public static IEndpointRouteBuilder MapDocumentosEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/documentos/{id:guid}/archivo", async (
            Guid id, IMediator mediator, IFileStorageService almacenamiento, CancellationToken cancellationToken) =>
        {
            var documento = await mediator.Send(new ObtenerDocumentoPorIdQuery(id), cancellationToken);
            if (documento?.ArchivoUrl is null)
                return Results.NotFound();

            var flujo = await almacenamiento.AbrirAsync(documento.ArchivoUrl, cancellationToken);
            return Results.File(flujo, "application/pdf", $"{documento.TipoDocumentoNombre}.pdf");
        });

        endpoints.MapGet("/documentos/plantilla.xlsx", (IPlantillaDocumentosService servicio) =>
            Results.File(
                servicio.GenerarPlantilla(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "plantilla-documentos.xlsx"));

        return endpoints;
    }
}
