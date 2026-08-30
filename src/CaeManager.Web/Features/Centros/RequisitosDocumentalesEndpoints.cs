using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;

namespace CaeManager.Web.Features.Centros;

/// <summary>
/// Sirve la plantilla en blanco adjunta a un TipoDocumentoCentro (Requisitos
/// del Centro, PLAN-EJECUCION-UX.md § 0.4) vía un endpoint autenticado —
/// mismo motivo que DocumentosEndpoints: IFileStorageService guarda fuera de
/// wwwroot, nunca como archivo estático público.
/// </summary>
public static class RequisitosDocumentalesEndpoints
{
    public static IEndpointRouteBuilder MapRequisitosDocumentalesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/requisitos-documentales/{id:guid}/archivo", async (
            Guid id, ITipoDocumentoCentroRepository repositorio, IAlcanceDatosService alcanceDatos,
            IFileStorageService almacenamiento, CancellationToken cancellationToken) =>
        {
            var fila = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
            if (fila?.ArchivoUrl is null || !await alcanceDatos.CentroVisibleAsync(fila.CentroId, cancellationToken))
                return Results.NotFound();

            var flujo = await almacenamiento.AbrirAsync(fila.ArchivoUrl, cancellationToken);
            var nombreArchivo = fila.NombreArchivoOriginal ?? "formulario.pdf";
            var tipoContenido = nombreArchivo.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
                ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                : "application/pdf";

            return Results.File(flujo, tipoContenido, nombreArchivo, enableRangeProcessing: true);
        });

        return endpoints;
    }
}
