using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;

namespace CaeManager.Web.Features.Centros;

/// <summary>
/// Sirve la plantilla en blanco adjunta a un TipoDocumentoCentro (Requisitos
/// del Centro, Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.4) vía un endpoint autenticado —
/// mismo motivo que DocumentosEndpoints: IFileStorageService guarda fuera de
/// wwwroot, nunca como archivo estático público.
/// </summary>
public static class RequisitosDocumentalesEndpoints
{
    public static IEndpointRouteBuilder MapRequisitosDocumentalesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/requisitos-documentales/{id:guid}/archivo", ServirPlantillaAsync);

        return endpoints;
    }

    /// <summary>
    /// Manejador de <c>GET /requisitos-documentales/{id}/archivo</c>. Método con nombre, y no una
    /// lambda, para poder probarlo sin levantar el host (mismo criterio que
    /// <c>DocumentosEndpoints.ServirArchivoAsync</c>).
    /// </summary>
    public static async Task<IResult> ServirPlantillaAsync(
        Guid id, ITipoDocumentoCentroRepository repositorio, IAlcanceDatosService alcanceDatos,
        IFileStorageService almacenamiento, ILoggerFactory fabricaRegistro, CancellationToken cancellationToken)
    {
        var fila = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
        if (fila?.ArchivoUrl is null || !await alcanceDatos.CentroVisibleAsync(fila.CentroId, cancellationToken))
            return Results.NotFound();

        // No pasa por IRegistroAccesoDocumentoSensibleService (DEC-36,
        // HO-099-01 § 6-7): TipoDocumentoCentro.ArchivoUrl es "la
        // plantilla en blanco a rellenar, no un justificante" (ver el
        // comentario de la entidad) — sin Documento ni persona detrás,
        // no hay contenido que DEC-36 pida registrar aunque el
        // TipoDocumentoId asociado sea sensible.
        Stream flujo;
        try
        {
            flujo = await almacenamiento.AbrirAsync(fila.ArchivoUrl, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            // La fila dice tener plantilla, pero el almacén no la tiene: 404 y un aviso, como en
            // DocumentosEndpoints, y no la excepción sin controlar. El aviso lleva el Id de la fila
            // y no la excepción ni la clave del fichero: la ruta del almacén no sale ni en la
            // respuesta ni en el registro.
            fabricaRegistro.CreateLogger(typeof(RequisitosDocumentalesEndpoints).FullName!).LogWarning(
                "El requisito documental de Centro {TipoDocumentoCentroId} tiene plantilla registrada pero su fichero no está en el almacén; se responde 404.",
                id);
            return Results.NotFound();
        }

        var nombreArchivo = fila.NombreArchivoOriginal ?? "formulario.pdf";
        var tipoContenido = nombreArchivo.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
            ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
            : "application/pdf";

        return Results.File(flujo, tipoContenido, nombreArchivo, enableRangeProcessing: true);
    }
}
