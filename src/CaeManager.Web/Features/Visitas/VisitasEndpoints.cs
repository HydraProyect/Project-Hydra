using CaeManager.Application.Visitas.Queries.ObtenerPaqueteDocumentalVisita;
using CaeManager.Application.Visitas.Queries.ObtenerSolicitudAccesoCorreo;
using MediatR;

namespace CaeManager.Web.Features.Visitas;

/// <summary>
/// Descarga del zip de documentación de una Visita a un Centro gestionado por correo
/// (P1-X1): el Gestor CAE lo adjunta a mano al correo de solicitud de acceso, sin M365
/// (D-1). Endpoint autenticado (FallbackPolicy global), nunca un archivo estático.
/// Autorización, selección y registro de accesos sensibles (DEC-36) viven en
/// <see cref="ObtenerPaqueteDocumentalVisitaQuery"/>, no aquí.
/// </summary>
public static class VisitasEndpoints
{
    public static IEndpointRouteBuilder MapVisitasEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/visitas/{id:guid}/paquete-documental.zip", async (
            Guid id, HttpContext contexto, IMediator mediator, CancellationToken cancellationToken) =>
        {
            var resultado = await mediator.Send(new ObtenerPaqueteDocumentalVisitaQuery(id), cancellationToken);

            if (resultado.EsFallido)
            {
                // Fuera de alcance responde igual que inexistente; el resto son
                // estados de la Visita que el Gestor CAE puede entender.
                return resultado.Error.Equals(ObtenerSolicitudAccesoCorreoQueryHandler.NoEncontrada)
                    ? Results.NotFound()
                    : Results.Text(resultado.Error.Mensaje, "text/plain; charset=utf-8", statusCode: StatusCodes.Status409Conflict);
            }

            CabecerasArchivoSensible.ProhibirCache(contexto);
            return Results.File(resultado.Valor.Contenido, "application/zip", resultado.Valor.NombreArchivo);
        });

        return endpoints;
    }
}
