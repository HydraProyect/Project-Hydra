using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Usuarios;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Exportacion;

/// <summary>
/// Quién puede pedir y descargar la exportación completa en autoservicio (#2008, D-8): solo el Administrador del
/// Tenant propietario, actuando él mismo, con un TOTP reciente.
/// <list type="bullet">
/// <item>Rol efectivo Administrador en el Tenant en curso y sin contexto cruzado: un Gestor CAE de un Operador CAE
/// externo que entra por Asignación de Operación nunca, aunque su perfil de origen sea Administrador.</item>
/// <item>Y Administrador de ese Tenant leído en la base (<see cref="IAdministradorDelTenantPropietario"/>), no solo en
/// la claim, como la seguridad de acceso del Tenant.</item>
/// <item>El Tenant en curso no es el de plataforma TALVEG.</item>
/// <item>Sin Sesión Privilegiada (revalidada en la base) y en vía normal: ni impersonación, ni Operación delegada, ni
/// Encargo de administración. El Actor real es el usuario de la sesión.</item>
/// <item>TOTP superado en esta sesión y reciente (<see cref="ICurrentUserService.SuperoTotpEnSesionAsync"/>).</item>
/// </list>
/// Las peticiones llevan además el área <c>Exportacion</c>, excluida del encargo
/// (<see cref="Tenants.Encargo.ActosExcluidosDelEncargo"/>).
/// </summary>
public static class AutoridadExportacionCompleta
{
    public const string RolAdministrador = "Administrador";

    public static readonly Error SoloAdministradorDelTenant = Error.Crear(
        "ExportacionCompleta.SoloAdministradorDelTenant",
        "Solo el Administrador de esta organización puede pedir y descargar la exportación completa de sus datos.");

    public static readonly Error SinSesionPrivilegiadaNiSuplantacion = Error.Crear(
        "ExportacionCompleta.SoloElPropioAdministrador",
        "La exportación completa solo la pide y la descarga el propio Administrador, sin Sesión Privilegiada ni acceso en nombre de otro.");

    public static readonly Error TotpReciente = Error.Crear(
        "ExportacionCompleta.TotpReciente",
        "Para la exportación completa tienes que haber confirmado tu código de verificación en dos pasos hace poco. Vuelve a confirmarlo e inténtalo otra vez.");

    /// <summary>El usuario y el Tenant que autorizan, o el error de la primera regla que no se cumple.</summary>
    public static async Task<(Error? Error, Guid UsuarioId, Guid TenantId)> ComprobarAsync(
        ICurrentUserService usuarioActual,
        ITenantActual tenantActual,
        IActorAuditoria actorAuditoria,
        ISesionPrivilegiadaActual sesionPrivilegiada,
        IAdministradorDelTenantPropietario administradorDelTenant,
        ITenantsQueryContext tenants,
        CancellationToken cancellationToken)
    {
        if (await sesionPrivilegiada.RevalidarAsync(cancellationToken) is not null)
            return (SinSesionPrivilegiadaNiSuplantacion, Guid.Empty, Guid.Empty);

        var actor = await actorAuditoria.ObtenerAsync();
        if (actor.UsuarioSimuladoId is not null || actor.Via != TipoViaAcceso.Normal || actor.EncargoAdministracionId is not null)
            return (SinSesionPrivilegiadaNiSuplantacion, Guid.Empty, Guid.Empty);

        if (tenantActual.TenantId is not { } tenantId
            || await usuarioActual.ObtenerUsuarioActualIdAsync() is not { } usuarioId
            || actor.ActorRealUsuarioId != usuarioId)
            return (SoloAdministradorDelTenant, Guid.Empty, Guid.Empty);

        if (await usuarioActual.ObtenerRolEfectivoAsync() != RolAdministrador
            || RolesReservadosAlTenantDeOrigen.EsContextoCruzado(await usuarioActual.ObtenerTenantOrigenIdAsync(), tenantId)
            || !await administradorDelTenant.EsAdministradorEnBaseAsync(usuarioId, tenantId, cancellationToken)
            || !await tenants.Tenants.AnyAsync(t => t.Id == tenantId && !t.EsPlataforma, cancellationToken))
            return (SoloAdministradorDelTenant, Guid.Empty, Guid.Empty);

        if (!await usuarioActual.SuperoTotpEnSesionAsync())
            return (TotpReciente, Guid.Empty, Guid.Empty);

        return (null, usuarioId, tenantId);
    }
}

/// <summary>
/// Las filas de auditoría propias de la exportación completa, una por acto: solicitud, generación (lista o
/// fallida), descarga y caducidad. Ninguna lleva la contraseña ni la ubicación del fichero.
/// </summary>
public static class AuditoriaExportacionCompleta
{
    public const string EntidadTipo = "SolicitudExportacionCompleta";
    public const string FaseSolicitada = "Solicitada";
    public const string FaseLista = "Lista";
    public const string FaseFallida = "Fallida";
    public const string FaseDescargada = "Descargada";
    public const string FaseCaducada = "Caducada";

    /// <summary>Un acto de una persona autenticada (solicitud y descarga): el Actor real es el autor.</summary>
    public static RegistroAuditoria DelActor(ActorAuditoria actor, Guid solicitudId, string fase, object? datos = null) =>
        new(EntidadTipo, solicitudId, RegistroAuditoria.AccionExportacion, datosAntes: null,
            datosDespues: Serializar(fase, datos),
            usuarioId: actor.UsuarioSimuladoId ?? actor.ActorRealUsuarioId,
            tipoActor: (TipoActorAuditoria)actor.ResolverTipoActor(),
            actorRealUsuarioId: actor.ActorRealUsuarioId,
            viaAcceso: (TipoViaAccesoAuditoria)actor.Via,
            viaAccesoId: actor.ViaAccesoId);

    /// <summary>Un acto del servicio de fondo (generación y caducidad): sin autor, actor de sistema.</summary>
    public static RegistroAuditoria DelSistema(Guid solicitudId, string fase, object? datos = null) =>
        new(EntidadTipo, solicitudId, RegistroAuditoria.AccionExportacion, datosAntes: null,
            datosDespues: Serializar(fase, datos),
            usuarioId: null, tipoActor: TipoActorAuditoria.Sistema, viaAcceso: TipoViaAccesoAuditoria.Desconocida);

    private static string Serializar(string fase, object? datos) =>
        JsonSerializer.Serialize(new { fase, alcance = "ExportacionCompleta", via = "Autoservicio", datos });
}
