using CaeManager.Application.Common;
using CaeManager.Domain.RequisitosDocumentales;

namespace CaeManager.Web.Features.Centros;

/// <summary>
/// Sirve el formulario adjunto de un RequisitoDocumental vía un endpoint
/// autenticado — mismo motivo que DocumentosEndpoints: IFileStorageService
/// guarda fuera de wwwroot, nunca como archivo estático público.
/// </summary>
public static class RequisitosDocumentalesEndpoints
{
    public static IEndpointRouteBuilder MapRequisitosDocumentalesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/requisitos-documentales/{id:guid}/archivo", async (
            Guid id, IRequisitoDocumentalRepository repositorio, IAlcanceDatosService alcanceDatos,
            IFileStorageService almacenamiento, CancellationToken cancellationToken) =>
        {
            var requisito = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
            if (requisito?.ArchivoUrl is null || !await alcanceDatos.CentroVisibleAsync(requisito.CentroId, cancellationToken))
                return Results.NotFound();

            var flujo = await almacenamiento.AbrirAsync(requisito.ArchivoUrl, cancellationToken);
            var nombreArchivo = requisito.NombreArchivoOriginal ?? "formulario.pdf";
            var tipoContenido = nombreArchivo.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
                ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                : "application/pdf";

            return Results.File(flujo, tipoContenido, nombreArchivo);
        });

        return endpoints;
    }
}
