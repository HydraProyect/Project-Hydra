using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;

namespace CaeManager.Application.Usuarios.Commands.RestablecerSegundoFactor;

/// <summary>
/// Quién puede restablecer la verificación en dos pasos de una cuenta. Separado del
/// acto (<see cref="RestablecerSegundoFactorCommandHandler"/>: restablecer y dejar la
/// auditoría) para que cada camino autorizado sea una implementación y el acto sea
/// uno. Hay dos, y <see cref="AutorizacionRestablecerSegundoFactorCompuesta"/> elige:
/// <list type="bullet">
/// <item><see cref="AutorizacionRestablecerSegundoFactorAdministrador"/> (P0-8): un
/// Administrador del Tenant propietario sobre otra cuenta de su Tenant;</item>
/// <item><see cref="AutorizacionRestablecerSegundoFactorPorSoporte"/> (ADR-011 § 8.7, punto 3):
/// Soporte TALVEG, desde una Sesión Privilegiada con la capacidad
/// <c>RestablecimientoSegundoFactor</c>, sobre el Administrador único de ese
/// Tenant.</item>
/// </list>
/// </summary>
public interface IAutorizacionRestablecerSegundoFactor
{
    /// <summary>
    /// Por qué camino puede el actor actual restablecer la 2FA de
    /// <paramref name="usuarioId"/>, o el motivo por el que no. Una cuenta inexistente
    /// y una de otro Tenant dan el mismo error.
    /// </summary>
    Task<Result<ViaRestablecimientoSegundoFactor>> AutorizarAsync(Guid usuarioId, CancellationToken cancellationToken);
}

/// <summary>
/// El camino autorizado. No es decorativo: decide cómo se escribe. El del
/// Administrador escribe con Identity bajo el rol de runtime; el de Soporte TALVEG no
/// puede —su conexión lleva el rol de solo lectura— y escribe por la función de la
/// base que vuelve a comprobar la sesión (<see cref="SesionPrivilegiadaId"/>).
/// </summary>
/// <param name="SesionPrivilegiadaId">La Sesión Privilegiada que ampara el acto, o
/// <c>null</c> cuando lo hace un Administrador del propio Tenant.</param>
public sealed record ViaRestablecimientoSegundoFactor(Guid? SesionPrivilegiadaId)
{
    public static readonly ViaRestablecimientoSegundoFactor AdministradorDelTenant = new((Guid?)null);
}

/// <summary>
/// Elige el camino por lo único que los distingue sin ambigüedad: si la petición va
/// dentro de una Sesión Privilegiada. Dentro de una, solo el de Soporte TALVEG; fuera,
/// solo el del Administrador. Nunca se prueba uno y, si falla, el otro: un técnico
/// dentro de una sesión no puede caer al camino del Administrador, ni al revés.
/// </summary>
public class AutorizacionRestablecerSegundoFactorCompuesta(
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    AutorizacionRestablecerSegundoFactorAdministrador administrador,
    AutorizacionRestablecerSegundoFactorPorSoporte porSoporte)
    : IAutorizacionRestablecerSegundoFactor
{
    public async Task<Result<ViaRestablecimientoSegundoFactor>> AutorizarAsync(
        Guid usuarioId, CancellationToken cancellationToken) =>
        await sesionPrivilegiadaActual.RevalidarAsync(cancellationToken) is { } sesion
            ? await porSoporte.AutorizarAsync(sesion, usuarioId, cancellationToken)
            : await administrador.AutorizarAsync(usuarioId, cancellationToken);
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
/// <item>la cuenta afectada pertenece a ese mismo Tenant. Nunca entre Tenants. Desde
/// P1-M1 la RLS de <c>AspNetUsers</c> también lo impide en la base: la política de
/// modificación solo deja escribir una cuenta del Tenant activo (o la propia). Esta
/// comprobación sigue siendo necesaria, porque la de lectura es más ancha (deja ver a
/// Operadores Delegados, Gestores CAE con cartera y actores de la auditoría) y es la que
/// da un error de negocio en vez de una excepción de base;</item>
/// <item>la cuenta afectada no es la suya: la propia se recupera con un código de
/// recuperación.</item>
/// </list>
/// Soporte TALVEG nunca por este camino: bajo Sesión Privilegiada el rol efectivo es
/// null, y <see cref="AutorizacionRestablecerSegundoFactorCompuesta"/> ni siquiera lo
/// consulta; el suyo es <see cref="AutorizacionRestablecerSegundoFactorPorSoporte"/>.
/// </summary>
public class AutorizacionRestablecerSegundoFactorAdministrador(
    ICurrentUserService currentUserService,
    IActorAuditoria actorAuditoria,
    ITenantActual tenantActual,
    ISegundoFactorDeCuentas segundoFactor)
    : IAutorizacionRestablecerSegundoFactor
{
    private const string RolAdministrador = "Administrador";

    public async Task<Result<ViaRestablecimientoSegundoFactor>> AutorizarAsync(
        Guid usuarioId, CancellationToken cancellationToken)
    {
        var resultado = await ComprobarAsync(usuarioId, cancellationToken);
        return resultado.EsFallido
            ? Result.Fallo<ViaRestablecimientoSegundoFactor>(resultado.Error)
            : Result.Exito(ViaRestablecimientoSegundoFactor.AdministradorDelTenant);
    }

    private async Task<Result> ComprobarAsync(Guid usuarioId, CancellationToken cancellationToken)
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

/// <summary>
/// El camino de Soporte TALVEG (ADR-011 § 8.7, punto 3; opción B de P0-8): restablecer la 2FA
/// del <b>Administrador único</b> de un Tenant que perdió el móvil y los códigos.
/// Todas a la vez:
/// <list type="bullet">
/// <item>la Sesión Privilegiada, revalidada contra la base en este comando, tiene la
/// capacidad <see cref="CapacidadPrivilegio.RestablecimientoSegundoFactor"/>, que solo
/// se concede por Tenant y solo por un AdminPlataforma a otro usuario de plataforma.
/// La concesión global de <c>SoporteLectura</c> no basta;</item>
/// <item>sin simulación de usuario;</item>
/// <item>el Tenant operado es el Tenant objetivo de la sesión;</item>
/// <item>la cuenta pertenece a ese Tenant (bajo la RLS, <c>AspNetUsers</c> no filtra
/// por Tenant);</item>
/// <item>es el único Administrador activo del Tenant. Si hay otro, lo restablece él
/// desde Usuarios (P0-8): Soporte TALVEG no sustituye a un Administrador que
/// existe.</item>
/// </list>
/// No da a Soporte TALVEG ningún rol en el Tenant: no es Administrador, ni Gestor CAE,
/// ni Operador CAE. Y esta comprobación no es la última: la función
/// <c>app_restablecer_segundo_factor_por_soporte</c> repite en la base la sesión, la
/// concesión y la cuenta antes de escribir.
/// </summary>
public class AutorizacionRestablecerSegundoFactorPorSoporte(
    ITenantActual tenantActual,
    ISegundoFactorDeCuentas segundoFactor)
{
    public async Task<Result<ViaRestablecimientoSegundoFactor>> AutorizarAsync(
        SesionPrivilegiadaActiva sesion, Guid usuarioId, CancellationToken cancellationToken)
    {
        if (sesion.Capacidad != CapacidadPrivilegio.RestablecimientoSegundoFactor)
            return Result.Fallo<ViaRestablecimientoSegundoFactor>(Error.Crear(
                "SegundoFactor.SesionSinCapacidad",
                "Esta sesión de soporte no permite restablecer la verificación en dos pasos. Hace falta una concesión específica para este Tenant."));

        if (sesion.UsuarioSimuladoId is not null)
            return Result.Fallo<ViaRestablecimientoSegundoFactor>(Error.Crear(
                "SegundoFactor.ActorNoTitular",
                "El restablecimiento no se hace simulando a otro usuario."));

        if (tenantActual.TenantId != sesion.TenantObjetivoId)
            return Result.Fallo<ViaRestablecimientoSegundoFactor>(Error.Crear(
                "SegundoFactor.FueraDelTenantObjetivo",
                "Esta sesión solo puede actuar en el Tenant sobre el que se abrió."));

        var estado = await segundoFactor.ObtenerEstadoAsync(usuarioId, cancellationToken);
        if (estado is null || estado.TenantId != sesion.TenantObjetivoId)
            return Result.Fallo<ViaRestablecimientoSegundoFactor>(RestablecerSegundoFactorCommandHandler.CuentaNoEncontrada);

        if (!await segundoFactor.EsAdministradorUnicoActivoAsync(usuarioId, sesion.TenantObjetivoId, cancellationToken))
            return Result.Fallo<ViaRestablecimientoSegundoFactor>(Error.Crear(
                "SegundoFactor.NoEsAdministradorUnico",
                "Soporte TALVEG solo restablece la verificación del Administrador único. Si el Tenant tiene otro Administrador activo, lo hace él desde Usuarios."));

        return Result.Exito(new ViaRestablecimientoSegundoFactor(sesion.SesionId));
    }
}
