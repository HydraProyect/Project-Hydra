using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;

namespace CaeManager.Application.Tenants.Encargo;

/// <summary>Quién registra o retira, ya resuelto: el Actor real y la vía por la que tiene autoridad.</summary>
public sealed record AutoridadParaElEncargo(Guid ActorRealUsuarioId, OrigenEncargoAdministracion Origen);

/// <summary>
/// Quién puede registrar, retirar y leer el Encargo de administración de un
/// Tenant propietario (decisión D-8, 2026-10-08). Es el tercero de los tres
/// actos excluidos: <b>el propio encargo nunca lo gobierna quien lo recibe.</b>
///
/// <para>
/// Solo dos actores, y ninguno se decide con el rol efectivo —que dentro de un
/// Tenant propietario con encargo vigente puede ser Administrador para una
/// persona del Operador CAE externo, justo a quien hay que dejar fuera—:
/// <list type="bullet">
/// <item><b>Plataforma</b>: una Sesión Privilegiada vigente sobre ESE Tenant
/// propietario, con la capacidad <see cref="CapacidadPrivilegio.Aprovisionamiento"/>
/// y sin simulación. Origen <see cref="OrigenEncargoAdministracion.AprovisionamientoDePlataforma"/>.</item>
/// <item><b>Administrador propio</b>: cuenta <i>miembro</i> del Tenant
/// propietario con rol Administrador, pertenencia y rol leídos de la base
/// (<see cref="IAdministradorDelTenantPropietario"/>). Origen
/// <see cref="OrigenEncargoAdministracion.AdministradorPropio"/>.</item>
/// </list>
/// Con una Sesión Privilegiada abierta que no cumple, se deniega sin probar la
/// segunda vía: un técnico de soporte no pasa a ser Administrador propio.
/// </para>
///
/// <para>
/// <b>Y nunca nadie del Operador CAE</b> (<see cref="EsDelOperadorCaeAsync"/>):
/// rechazo explícito si el Tenant de origen de quien actúa es el Operador CAE
/// externo del encargo, aunque las dos vías de arriba ya lo excluyan por
/// construcción. Son dos predicados distintos para que quitar uno no abra el
/// acto; la política RLS <c>posicion_en_el_encargo</c> repite el segundo en la
/// base de datos.
/// </para>
///
/// <para>
/// El Actor real que se guarda sale de <see cref="IActorAuditoria"/>, no del
/// usuario de la sesión a secas: cuando exista la impersonación, el encargo lo
/// habrá registrado quien estaba detrás del teclado, y un acto simulado no
/// registra ni retira encargos.
/// </para>
/// </summary>
public class AutoridadSobreElEncargo(
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    ICurrentUserService currentUserService,
    IActorAuditoria actorAuditoria,
    IAdministradorDelTenantPropietario administradorDelTenant)
{
    public static readonly Error NoAutorizado = Error.Crear(
        "Encargo.NoAutorizado",
        "El encargo de administración solo lo registra o lo retira un Administrador propio de la organización " +
        "o el soporte de TALVEG en una sesión de aprovisionamiento. Nunca la organización que lo recibe.");

    /// <summary>
    /// La autoridad de quien actúa sobre los encargos de
    /// <paramref name="propietarioTenantId"/>, o <c>null</c> si no la tiene.
    /// </summary>
    public async Task<AutoridadParaElEncargo?> ResolverAsync(
        Guid propietarioTenantId, CancellationToken cancellationToken = default)
    {
        if (propietarioTenantId == Guid.Empty) return null;

        var actor = await actorAuditoria.ObtenerAsync();
        if (actor.ActorRealUsuarioId is not { } actorReal || actor.UsuarioSimuladoId is not null)
            return null;

        // ObtenerAsync y no RevalidarAsync, mismo motivo que AutorizacionLogoTenant: el pipeline del
        // comando acaba de revalidar la sesión en este ámbito, y volver a leer la tabla de sesiones
        // con la conexión ya elevada a cae_app_aprovisionamiento fallaría por permisos.
        if (await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is { } sesion)
            return sesion.Capacidad == CapacidadPrivilegio.Aprovisionamiento
                   && sesion.TieneCaminoDeEscritura
                   && sesion.TenantObjetivoId == propietarioTenantId
                   && sesion.UsuarioSimuladoId is null
                ? new AutoridadParaElEncargo(actorReal, OrigenEncargoAdministracion.AprovisionamientoDePlataforma)
                : null;

        if (await currentUserService.ObtenerUsuarioActualIdAsync() != actorReal)
            return null;

        return await administradorDelTenant.EsAdministradorEnBaseAsync(actorReal, propietarioTenantId, cancellationToken)
            ? new AutoridadParaElEncargo(actorReal, OrigenEncargoAdministracion.AdministradorPropio)
            : null;
    }

    /// <summary>
    /// Quien actúa pertenece al Operador CAE externo del encargo. Falla cerrado:
    /// sin Tenant de origen resuelto se trata como si lo fuera.
    /// </summary>
    public async Task<bool> EsDelOperadorCaeAsync(Guid operadorTenantId) =>
        await currentUserService.ObtenerTenantOrigenIdAsync() is not { } tenantOrigenId
        || tenantOrigenId == operadorTenantId;
}
