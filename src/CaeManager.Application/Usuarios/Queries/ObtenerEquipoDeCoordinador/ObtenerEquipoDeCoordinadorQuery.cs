using CaeManager.Application.Common;
using CaeManager.Application.Usuarios.Commands.AsignarCarteraGestorCae;
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
/// Vacía si quien pregunta no es Coordinador CAE como rol efectivo de la sesión en el Tenant de
/// origen, confirmado en Identity sobre la cuenta activa de su Operador CAE
/// (<see cref="Commands.AsignarCarteraGestorCae.AutoridadSobreCarteraDeGestorCae.RolDeAutoridadAsync"/>). Administrador y Dirección CAE usan la lista completa
/// de siempre y no pasan por aquí.
/// </summary>
public record ObtenerEquipoDeCoordinadorQuery : IRequest<IReadOnlyList<MiembroDeEquipo>>;

public class ObtenerEquipoDeCoordinadorQueryHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    IDirectorioEquipoCoordinador equipo)
    : IRequestHandler<ObtenerEquipoDeCoordinadorQuery, IReadOnlyList<MiembroDeEquipo>>
{
    private static readonly string[] RolQueLee = ["CoordinadorCae"];

    public async Task<IReadOnlyList<MiembroDeEquipo>> Handle(
        ObtenerEquipoDeCoordinadorQuery request, CancellationToken cancellationToken)
    {
        var actorId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var origen = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (actorId is null || origen is null)
            return [];

        using (AmbitoTenantExplicito.Establecer(origen.Value))
        {
            // Rol efectivo de la sesión en el Tenant de origen, confirmado en Identity: una sesión
            // sin rol de negocio (Soporte TALVEG) o limitada a Consulta no recibe el equipo.
            if (await AutoridadSobreCarteraDeGestorCae.RolDeAutoridadAsync(
                    actorId.Value, origen.Value, RolQueLee, currentUserService, directorioUsuarios, cancellationToken) is null)
                return [];

            return await equipo.ObtenerEquipoAsync(actorId.Value, cancellationToken);
        }
    }
}
