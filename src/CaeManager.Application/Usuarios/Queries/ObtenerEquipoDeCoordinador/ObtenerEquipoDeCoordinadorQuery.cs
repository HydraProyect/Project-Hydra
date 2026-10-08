using CaeManager.Application.Common;
using MediatR;

namespace CaeManager.Application.Usuarios;

/// <summary>Un Gestor CAE del equipo de un Coordinador CAE, tal y como lo lista /usuarios para él.</summary>
/// <param name="Avatar">Clave del avatar elegido por esa persona (<see cref="CatalogoAvatares"/>), o <c>null</c>: iniciales.</param>
public record MiembroDeEquipo(
    Guid Id, string Email, string NombreCompleto, bool Activo, bool PendienteActivacion, string? Avatar = null);

/// <summary>
/// Los Gestores CAE de la propia organización que reportan a <c>coordinadorUsuarioId</c>
/// (<c>CoordinadorUsuarioId</c>). Puerto porque <c>ApplicationUser</c> vive en
/// Infrastructure.Identity. No decide quién puede preguntar: lo decide
/// <c>ObtenerEquipoDeCoordinadorQuery</c>.
/// </summary>
public interface IDirectorioEquipoCoordinador
{
    Task<IReadOnlyList<MiembroDeEquipo>> ObtenerEquipoAsync(Guid coordinadorUsuarioId, CancellationToken cancellationToken = default);
}

/// <summary>
/// La lista de /usuarios para un Coordinador CAE: solo su equipo. El filtro vive aquí, no en la
/// interfaz: la página no lee a ningún otro usuario cuando quien la abre es un Coordinador CAE.
/// Vacía si quien pregunta no es un Coordinador CAE activo de su Operador CAE (leído en Identity
/// sobre el Tenant de origen, no en el claim). Administrador y Dirección CAE usan la lista completa
/// de siempre y no pasan por aquí.
/// </summary>
public record ObtenerEquipoDeCoordinadorQuery : IRequest<IReadOnlyList<MiembroDeEquipo>>;

public class ObtenerEquipoDeCoordinadorQueryHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    IDirectorioEquipoCoordinador equipo)
    : IRequestHandler<ObtenerEquipoDeCoordinadorQuery, IReadOnlyList<MiembroDeEquipo>>
{
    private const string CoordinadorCae = "CoordinadorCae";

    public async Task<IReadOnlyList<MiembroDeEquipo>> Handle(
        ObtenerEquipoDeCoordinadorQuery request, CancellationToken cancellationToken)
    {
        var actorId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var origen = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (actorId is null || origen is null)
            return [];

        using (AmbitoTenantExplicito.Establecer(origen.Value))
        {
            if (await currentUserService.ObtenerRolEfectivoAsync() is null
                || !await directorioUsuarios.EsCuentaActivaConRolAsync(actorId.Value, origen.Value, CoordinadorCae, cancellationToken))
                return [];

            return await equipo.ObtenerEquipoAsync(actorId.Value, cancellationToken);
        }
    }
}
