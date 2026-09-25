using CaeManager.Application.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Usuarios.Commands.RestablecerSegundoFactor;

/// <summary>
/// Quién puede restablecer la verificación en dos pasos de una cuenta. Separado del
/// acto (<see cref="RestablecerSegundoFactorCommandHandler"/>: restablecer y dejar la
/// auditoría) para que un segundo camino autorizado —Soporte TALVEG con Sesión
/// Privilegiada y una capacidad acotada, que implementa otra línea— se añada como otra
/// implementación o una composición de esta, sin duplicar el acto. Hoy solo existe
/// <see cref="AutorizacionRestablecerSegundoFactorAdministrador"/>.
/// </summary>
public interface IAutorizacionRestablecerSegundoFactor
{
    /// <summary>
    /// Éxito si el actor actual puede restablecer la 2FA de <paramref name="usuarioId"/>.
    /// Una cuenta inexistente y una de otro Tenant dan el mismo error.
    /// </summary>
    Task<Result> AutorizarAsync(Guid usuarioId, CancellationToken cancellationToken);
}

/// <summary>
/// El único camino autorizado hoy (P0-8, FS-01): un Administrador del Tenant
/// propietario de la cuenta, sobre otra cuenta de ese mismo Tenant. Todas a la vez:
/// <list type="bullet">
/// <item>rol efectivo Administrador en el Tenant operado;</item>
/// <item>sin simulación de usuario: el Actor real es quien ejecuta;</item>
/// <item>operando su propio Tenant de origen: el Tenant operado es el Tenant
/// propietario de la cuenta del actor. Un Workspace operativo derivado nunca da
/// Administrador (la Operación no concede roles de Propiedad), pero se comprueba
/// aquí igualmente, no se hereda de esa regla;</item>
/// <item>la cuenta afectada pertenece a ese mismo Tenant. Nunca entre Tenants: bajo el
/// rol de runtime <c>AspNetUsers</c> no filtra por Tenant, así que esta comprobación es
/// la única barrera;</item>
/// <item>la cuenta afectada no es la suya: la propia se recupera con un código de
/// recuperación.</item>
/// </list>
/// Soporte TALVEG nunca por este camino: bajo Sesión Privilegiada el rol efectivo es
/// null, y <c>AutorizacionEscrituraBehavior</c> tampoco deja llegar el comando.
/// </summary>
public class AutorizacionRestablecerSegundoFactorAdministrador(
    ICurrentUserService currentUserService,
    IActorAuditoria actorAuditoria,
    ITenantActual tenantActual,
    ISegundoFactorDeCuentas segundoFactor)
    : IAutorizacionRestablecerSegundoFactor
{
    private const string RolAdministrador = "Administrador";

    public async Task<Result> AutorizarAsync(Guid usuarioId, CancellationToken cancellationToken)
    {
        var actorId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (actorId is null)
            return Result.Fallo(Error.Crear("SegundoFactor.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        if (await currentUserService.ObtenerRolEfectivoAsync() != RolAdministrador)
            return Result.Fallo(Error.Crear(
                "SegundoFactor.SoloAdministrador",
                "Solo un Administrador de tu organización puede restablecer la verificación en dos pasos de otra persona."));

        var actor = await actorAuditoria.ObtenerAsync();
        if (actor.UsuarioSimuladoId is not null || actor.ActorRealUsuarioId != actorId)
            return Result.Fallo(Error.Crear(
                "SegundoFactor.ActorNoTitular",
                "Esta acción solo la puede hacer un Administrador con su propia sesión."));

        var tenantOperado = tenantActual.TenantId;
        var tenantOrigen = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (tenantOperado is null || tenantOrigen != tenantOperado)
            return Result.Fallo(Error.Crear(
                "SegundoFactor.FueraDelTenantPropietario",
                "Solo puedes restablecer la verificación en dos pasos de las cuentas de tu propia organización."));

        if (usuarioId == actorId)
            return Result.Fallo(Error.Crear(
                "SegundoFactor.PropiaCuenta",
                "No puedes restablecer tu propia verificación en dos pasos. Si no tienes el móvil, entra con un código de recuperación."));

        var estado = await segundoFactor.ObtenerEstadoAsync(usuarioId, cancellationToken);
        if (estado is null || estado.TenantId != tenantOperado)
            return Result.Fallo(RestablecerSegundoFactorCommandHandler.CuentaNoEncontrada);

        return Result.Exito();
    }
}
