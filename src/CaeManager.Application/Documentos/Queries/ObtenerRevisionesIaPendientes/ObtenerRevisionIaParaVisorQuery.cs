using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;

/// <summary>
/// La revisión pendiente cuyo documento sirve <c>GET /documentos/revision-ia/{revisionId}/archivo</c>, el visor que la
/// pantalla Revisión IA carga sola al seleccionar (D-49, opción C). Devuelve el Documento de ESA revisión, que el cliente
/// no elige, o <c>null</c> si quien pide no la alcanza en la cola: rol efectivo sin acceso a la pantalla
/// (<see cref="ColaRevisionIa.AccedeElRol"/>; bajo una Sesión Privilegiada no hay rol efectivo), revisión inexistente, ya
/// resuelta, de otro Tenant propietario (filtro global y RLS) o de un Documento fuera de la Asignación de Cartera. Todos los
/// casos se contestan igual, para no revelar si la revisión existe.
/// </summary>
public record ObtenerRevisionIaParaVisorQuery(Guid RevisionId) : IRequest<RevisionIaParaVisorDto?>;

/// <param name="DocumentoId">El Documento de la revisión: el único que la ruta puede servir.</param>
/// <param name="CreadaEnUtc">Desde cuándo cuenta una fila <c>RevisionIa</c> ya escrita como la de esta revisión.</param>
public record RevisionIaParaVisorDto(Guid DocumentoId, DateTime CreadaEnUtc);

public class ObtenerRevisionIaParaVisorQueryHandler(
    IDocumentosQueryContext documentosContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerRevisionIaParaVisorQuery, RevisionIaParaVisorDto?>
{
    public async Task<RevisionIaParaVisorDto?> Handle(ObtenerRevisionIaParaVisorQuery request, CancellationToken cancellationToken)
    {
        // El rol EFECTIVO, no el claim: es la misma fuente con la que la cola y el filtro del listado deciden. Sin rol, nada.
        if (!ColaRevisionIa.AccedeElRol(await currentUserService.ObtenerRolEfectivoAsync()))
            return null;

        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);

        // La misma definición de «pendiente y al alcance» que la pestaña: lo que no está en la cola de quien pide, no se sirve.
        return await ColaRevisionIa.Pendientes(documentosContext, trabajadorIdsVisibles)
            .Where(p => p.Revision.Id == request.RevisionId)
            .Select(p => new RevisionIaParaVisorDto(p.Revision.DocumentoId, p.Revision.CreadaEnUtc))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
