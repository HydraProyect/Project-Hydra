using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento.AceptacionContractual;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Cumplimiento.Subencargados;

public static class ErroresSubencargado
{
    public static readonly Error SinUsuario = Error.Crear(
        "Subencargado.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión.");

    public static readonly Error SoloActorDePlataforma = Error.Crear(
        "Subencargado.SoloActorDePlataforma",
        "Solo un Actor de Plataforma TALVEG con administración de plataforma global, desde su propia cuenta, gestiona los subencargados.");

    public static readonly Error SinDobleFactor = Error.Crear(
        "Subencargado.SinDobleFactor", "Activa la verificación en dos pasos para gestionar los subencargados.");

    public static readonly Error TotpNoVerificado = Error.Crear(
        "Subencargado.TotpNoVerificado", "Vuelve a verificar tu segundo factor para gestionar los subencargados.");

    public static readonly Error SoloAdministradorDelTenantPropietario = Error.Crear(
        "Subencargado.SoloAdministradorDelTenantPropietario",
        "Solo el Administrador del Tenant propietario, desde su propia organización, puede objetar un subencargado.");

    public static readonly Error NoEncontrado = Error.Crear("Subencargado.NoEncontrado", "No encontramos ese subencargado.");

    public static readonly Error NoSeAvisa = Error.Crear(
        "Subencargado.NoSeAvisa", "Solo se avisa de un subencargado propuesto o ya en aviso, no de uno activo o retirado.");

    public static readonly Error EnvioNoAislado = Error.Crear(
        "Subencargado.EnvioNoAislado", "No podemos enviar los avisos en este momento.");

    public static readonly Error NoAdmiteObjecion = Error.Crear(
        "Subencargado.NoAdmiteObjecion", "Este subencargado no admite objeciones: todavía no se ha avisado o ya está retirado.");

    public static readonly Error YaObjetado = Error.Crear(
        "Subencargado.YaObjetado", "Tu organización ya registró una objeción a este subencargado.");

    public static readonly Error YaRetirado = Error.Crear("Subencargado.YaRetirado", "Este subencargado ya estaba retirado.");

    public static Error NoActivable(string motivo) => Error.Crear("Subencargado.NoActivable", motivo);
}

/// <summary>
/// Quién gestiona los subencargados y quién los objeta (#2010, D-7).
///
/// <para>
/// Proponer, avisar, activar y retirar: un Actor de Plataforma TALVEG con concesión AdminPlataforma global vigente,
/// desde su propia cuenta (fuera de una Sesión Privilegiada) y con el segundo factor activo y superado en esta
/// sesión. Es el criterio del aviso de incidentes de seguridad, que también escribe a los Administradores de todos
/// los Tenants propietarios (<c>AutorizacionAvisoIncidenteSeguridad</c>).
/// </para>
///
/// <para>
/// Objetar: solo el Administrador del Tenant propietario, con ese Tenant como Tenant de origen. No objeta nadie en
/// una Sesión Privilegiada (impersonación o Soporte TALVEG: el Actor real no es el Administrador), ni un Gestor CAE
/// o un Administrador de un Operador CAE externo que opera el Tenant (contexto cruzado), ni un Actor de Plataforma
/// TALVEG (la objeción es del Tenant propietario, no de quien propone el subencargado), ni nadie en el Tenant de
/// Plataforma.
/// </para>
/// </summary>
internal static class AutorizacionSubencargados
{
    public static async Task<Result<Guid>> ActorDePlataformaAsync(
        ICurrentUserService usuarioActual,
        ISesionPrivilegiadaActual sesionPrivilegiada,
        IAutorizacionAdminPlataforma autorizacion,
        CancellationToken cancellationToken)
    {
        if (await sesionPrivilegiada.ObtenerAsync(cancellationToken) is not null)
            return Result.Fallo<Guid>(ErroresSubencargado.SoloActorDePlataforma);
        if (await usuarioActual.ObtenerUsuarioActualIdAsync() is not { } usuarioId)
            return Result.Fallo<Guid>(ErroresSubencargado.SinUsuario);
        if (!await autorizacion.PuedeGlobalmenteAsync(usuarioId, cancellationToken))
            return Result.Fallo<Guid>(ErroresSubencargado.SoloActorDePlataforma);
        if (!await usuarioActual.TieneDobleFactorActivoAsync())
            return Result.Fallo<Guid>(ErroresSubencargado.SinDobleFactor);
        if (!await usuarioActual.SuperoTotpEnSesionAsync())
            return Result.Fallo<Guid>(ErroresSubencargado.TotpNoVerificado);
        return Result.Exito(usuarioId);
    }

    /// <summary>El Administrador del Tenant propietario actual y su Tenant, o el error.</summary>
    public static async Task<Result<(Guid UsuarioId, Guid TenantId)>> AdministradorDelTenantPropietarioAsync(
        ICurrentUserService usuarioActual,
        ITenantActual tenantActual,
        ISesionPrivilegiadaActual sesionPrivilegiada,
        IAutorizacionAdminPlataforma autorizacion,
        ITenantsQueryContext tenants,
        CancellationToken cancellationToken)
    {
        var denegado = Result.Fallo<(Guid, Guid)>(ErroresSubencargado.SoloAdministradorDelTenantPropietario);

        if (await sesionPrivilegiada.ObtenerAsync(cancellationToken) is not null)
            return denegado;
        if (await usuarioActual.ObtenerUsuarioActualIdAsync() is not { } usuarioId)
            return Result.Fallo<(Guid, Guid)>(ErroresSubencargado.SinUsuario);
        if (tenantActual.TenantId is not { } tenantId
            || !await AutoridadSobreAceptacionContractual.EsAdministradorDelTenantPropietarioAsync(usuarioActual, tenantActual))
            return denegado;
        if (await autorizacion.PuedeGlobalmenteAsync(usuarioId, cancellationToken))
            return denegado;
        if (!await tenants.Tenants.AnyAsync(t => t.Id == tenantId && !t.EsPlataforma, cancellationToken))
            return denegado;
        return Result.Exito((usuarioId, tenantId));
    }
}
