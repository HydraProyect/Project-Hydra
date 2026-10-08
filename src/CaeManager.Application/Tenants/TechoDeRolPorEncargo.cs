using CaeManager.Application.Operaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants;

/// <summary>
/// El rol efectivo por la vía de Operación y, si lo subió un Encargo de
/// administración, el Id de ese encargo.
/// </summary>
public readonly record struct RolEfectivoPorOperacion(string? Rol, Guid? EncargoAdministracionId)
{
    public static RolEfectivoPorOperacion Ninguno => new(null, null);
}

/// <summary>
/// <b>El punto ÚNICO donde un Encargo de administración sube el techo del rol
/// efectivo</b> (decisión D-8, 2026-10-08; ADR-011 § 2.7, enmienda del
/// 2026-10-08, punto 7). Lo llama solo <c>CurrentUserService</c>, en la vía de
/// Operación (selección normal y fan-out). La vía heredada nunca se eleva.
///
/// <para>
/// El encargo <b>no concede acceso</b>: sube el techo de quien ya lo tiene. Por
/// eso el orden importa y cada paso falla hacia el rol de cartera, nunca hacia
/// más:
/// <list type="number">
/// <item>Rol de cartera, con el predicado único
/// (<see cref="TenantsBeneficiariosAutorizados.RolPorOperacionAsync"/>).
/// <b>Sin cartera vigente no hay rol, haya o no encargo.</b></item>
/// <item>Solo se eleva una cartera de Gestor CAE o de Coordinador CAE. Una
/// cartera de Consulta es una concesión expresa de solo lectura: elevarla
/// convertiría una asignación de consulta en administración.</item>
/// <item>Tiene que haber un encargo vigente para ESA operación, cuyo Operador
/// CAE externo sea el Tenant de origen del usuario y cuyo Tenant propietario
/// sea el operado. El encargo de otro Operador CAE, o de otra operación, no
/// sirve.</item>
/// <item>La cuenta tiene perfil de Propiedad en su Tenant de origen
/// (<see cref="IPerfilDePropiedadEnOrigen"/>, leído de Identity).</item>
/// <item>El rol de la sesión en origen coincide con el de Identity. <b>La claim
/// nunca eleva, solo puede impedirlo</b>: una Dirección CAE rebajada a Consulta
/// por entrar con contraseña estando activo el SSO no se eleva.</item>
/// <item>Rol efectivo = el de Identity (Administrador o Dirección CAE).</item>
/// </list>
/// </para>
///
/// <para>
/// Lo que el rol elevado <b>no</b> abre se decide en otros sitios, no aquí: las
/// cuentas con rol de Propiedad (<c>Usuarios</c>), los Operadores CAE externos
/// (<see cref="IAutorizacionDelegacionTenant"/>), el propio encargo
/// (<see cref="Encargo.AutoridadSobreElEncargo"/>) y las áreas de
/// <c>ActosExcluidosDelEncargo</c>.
/// </para>
/// </summary>
public class TechoDeRolPorEncargo(
    IOperacionesQueryContext operaciones,
    IEncargosAdministracionQueryContext encargos,
    IPerfilDePropiedadEnOrigen perfilDePropiedadEnOrigen)
{
    /// <summary>Roles de cartera que un encargo puede elevar. Consulta queda fuera a propósito.</summary>
    public static readonly IReadOnlyList<string> RolesDeCarteraElevables = ["CoordinadorCae", "GestorCae"];

    /// <summary>Roles de Propiedad a los que se eleva: el perfil de la cuenta en su Tenant de origen.</summary>
    public static readonly IReadOnlyList<string> RolesDePropiedad = ["Administrador", "DireccionCae"];

    public async Task<RolEfectivoPorOperacion> ResolverAsync(
        Guid usuarioId, Guid tenantOrigenId, Guid tenantId, Guid asignacionOperacionId,
        string? rolDeSesionEnOrigen, DateTime ahora, CancellationToken cancellationToken)
    {
        // 1. Sin cartera vigente no hay rol, haya o no encargo.
        var rolDeCartera = await TenantsBeneficiariosAutorizados.RolPorOperacionAsync(
            operaciones, usuarioId, tenantOrigenId, tenantId, asignacionOperacionId, ahora, cancellationToken);
        if (rolDeCartera is null)
            return RolEfectivoPorOperacion.Ninguno;

        var soloCartera = new RolEfectivoPorOperacion(rolDeCartera, null);

        // 2. Consulta (y cualquier rol que no sea de gestión) no se eleva.
        if (!RolesDeCarteraElevables.Contains(rolDeCartera))
            return soloCartera;

        // 3. Encargo vigente de ESTA operación, de ESTE Operador CAE, sobre ESTE
        // Tenant propietario. Las tres igualdades hacen falta: la operación llega
        // elegida por el llamante, y el Operador CAE y el Tenant propietario se
        // comprueban contra la fila del encargo, no se dan por buenos.
        var encargoId = await encargos.EncargosAdministracion
            .Where(e => e.AsignacionOperacionId == asignacionOperacionId
                        && e.OperadorTenantId == tenantOrigenId
                        && e.PropietarioTenantId == tenantId
                        && e.RetiradoEnUtc == null
                        && e.VigenciaDesde <= ahora
                        && (e.VigenciaHasta == null || ahora < e.VigenciaHasta))
            .OrderBy(e => e.Id)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (encargoId is null)
            return soloCartera;

        // 4. Perfil de Propiedad en el Tenant de origen, de Identity.
        var perfil = await perfilDePropiedadEnOrigen.ObtenerAsync(usuarioId, tenantOrigenId, cancellationToken);
        if (perfil is null || !RolesDePropiedad.Contains(perfil))
            return soloCartera;

        // 5. La sesión tiene que decir lo mismo: la claim no eleva, solo impide.
        if (!string.Equals(rolDeSesionEnOrigen, perfil, StringComparison.Ordinal))
            return soloCartera;

        // 6. El perfil de origen, con el encargo que lo ampara.
        return new RolEfectivoPorOperacion(perfil, encargoId);
    }
}
