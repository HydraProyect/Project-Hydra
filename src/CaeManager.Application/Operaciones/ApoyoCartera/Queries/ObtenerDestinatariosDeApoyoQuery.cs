using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerCompanerosGestorCae;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Domain.Operaciones;
using MediatR;

namespace CaeManager.Application.Operaciones.ApoyoCartera.Queries;

/// <summary>Un Gestor CAE al que se puede proponer un apoyo. Solo el nombre: es una lista para elegir.</summary>
public record DestinatarioDeApoyoDto(Guid UsuarioId, string Nombre);

/// <summary>
/// A quién puede proponer un apoyo quien pregunta, sobre una Asignación de Operación: los demás
/// Gestores CAE activos de <b>su mismo Operador CAE</b> que todavía no tienen cartera viva bajo
/// esa operación ni una propuesta suya pendiente. Vacía si quien pregunta no lleva hoy la marca
/// de principal en esa operación: solo el principal propone.
///
/// <para>
/// Es una ayuda para elegir, no la autorización: <c>ProponerApoyoCarteraCommand</c> vuelve a
/// comprobarlo todo. El Operador CAE sale del Tenant de origen de la sesión, nunca de la
/// petición; el rol, de Identity sobre ese Tenant y no del claim.
/// </para>
/// </summary>
public record ObtenerDestinatariosDeApoyoQuery(Guid AsignacionOperacionId) : IRequest<IReadOnlyList<DestinatarioDeApoyoDto>>;

public class ObtenerDestinatariosDeApoyoQueryHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    IDirectorioCompanerosGestorCae gestoresCae,
    ICatalogoIncorporacionCartera catalogo,
    IPropuestaApoyoCarteraRepository repositorio)
    : IRequestHandler<ObtenerDestinatariosDeApoyoQuery, IReadOnlyList<DestinatarioDeApoyoDto>>
{
    public async Task<IReadOnlyList<DestinatarioDeApoyoDto>> Handle(
        ObtenerDestinatariosDeApoyoQuery request, CancellationToken cancellationToken)
    {
        var contexto = await ContextoOperadorCae.ResolverAsync(currentUserService, directorioUsuarios, cancellationToken);
        if (contexto.EsFallido || !contexto.Valor.ParticipaEnIncorporacionCartera)
            return [];
        var ctx = contexto.Valor;

        using (ctx.EnOrigen())
        {
            var carteras = (await catalogo.ObtenerCarterasVivasAsync(ctx.OperadorTenantId, null, cancellationToken))
                .Where(c => c.AsignacionOperacionId == request.AsignacionOperacionId)
                .ToList();
            if (carteras.FirstOrDefault(c => c.EsPrincipal)?.UsuarioId != ctx.UsuarioId)
                return [];

            var conCartera = carteras.Select(c => c.UsuarioId).ToHashSet();
            var conPropuesta = (await repositorio.ListarPendientesDelProponenteAsync(ctx.OperadorTenantId, ctx.UsuarioId, cancellationToken))
                .Where(p => p.AsignacionOperacionId == request.AsignacionOperacionId)
                .Select(p => p.DestinatarioUsuarioId)
                .ToHashSet();

            var gestores = await gestoresCae.ObtenerGestoresCaeDelOperadorAsync(
                ctx.OperadorTenantId, ctx.UsuarioId, cancellationToken);

            return gestores
                .Where(g => !conCartera.Contains(g.UsuarioId) && !conPropuesta.Contains(g.UsuarioId))
                .Select(g => new DestinatarioDeApoyoDto(g.UsuarioId, g.Nombre))
                .OrderBy(g => g.Nombre)
                .ToList();
        }
    }
}
