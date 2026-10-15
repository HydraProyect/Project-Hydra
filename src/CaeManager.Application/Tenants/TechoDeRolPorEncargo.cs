using CaeManager.Application.Operaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants;

/// <summary>
/// El rol efectivo por la vía de Operación y, si lo subió un Encargo de
/// administración, el Id de ese encargo.
/// </summary>
/// <param name="EncargoPleno">
/// El encargo que elevó es <b>pleno</b>: el Tenant propietario no tiene
/// Administrador propio, ni activo ni pendiente de activar, así que quien administra por encargo lo hace
/// sin excepciones (decisión de Chris, 2026-10-15). Falso si no elevó o si el
/// Tenant propietario tiene Administrador propio: entonces rigen las
/// exclusiones del 2026-10-08.
/// </param>
public readonly record struct RolEfectivoPorOperacion(string? Rol, Guid? EncargoAdministracionId, bool EncargoPleno = false)
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
/// <item>Esa cartera de gestión tiene que ser <b>del Tenant entero</b>: universal,
/// bajo una operación universal
/// (<see cref="TenantsBeneficiariosAutorizados.TieneCarteraDelTenantEnteroAsync"/>).
/// Un rol de Propiedad tiene alcance total sin consultar carteras, así que
/// elevar una cartera parcial ampliaría su ámbito a todo el Tenant. Con cartera
/// parcial la persona se queda con su rol de cartera y su alcance parcial.</item>
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
/// <item>El encargo es <b>pleno</b> si el Tenant propietario no tiene ningún
/// Administrador propio, ni activo ni pendiente de activar (<see cref="ITenantConAdministradorPropio"/>).
/// No cambia el rol: decide si rigen las exclusiones del 2026-10-08.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Encargo pleno (decisión de Chris, 2026-10-15).</b> En un Tenant
/// propietario sin Administrador propio, quien administra por encargo es
/// Administrador pleno, sin excepciones: no rigen las áreas de
/// <c>ActosExcluidosDelEncargo</c> ni la reserva de cuentas con rol de
/// Propiedad, y puede retirar su propio encargo. En un Tenant con Administrador
/// propio nada cambia respecto del 2026-10-08: siguen rigiendo las exclusiones
/// (cuentas con rol de Propiedad en <c>Usuarios</c>, el propio encargo en
/// <see cref="Encargo.AutoridadSobreElEncargo"/> y las áreas de
/// <c>ActosExcluidosDelEncargo</c>). La autoridad sigue viniendo del encargo:
/// sin él, la cartera sola nunca llega aquí con rol de Propiedad.
/// </para>
/// </summary>
public class TechoDeRolPorEncargo(
    IOperacionesQueryContext operaciones,
    IEncargosAdministracionQueryContext encargos,
    IPerfilDePropiedadEnOrigen perfilDePropiedadEnOrigen,
    ITenantConAdministradorPropio tenantConAdministradorPropio)
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

        // 3. Solo se eleva a quien su cartera ya da el Tenant entero. Un rol de
        // Propiedad tiene alcance total sin consultar carteras: elevar una
        // cartera parcial convertiría su ámbito acotado en todo el Tenant.
        if (!await TenantsBeneficiariosAutorizados.TieneCarteraDelTenantEnteroAsync(
                operaciones, usuarioId, tenantOrigenId, tenantId, asignacionOperacionId,
                RolesDeCarteraElevables, ahora, cancellationToken))
            return soloCartera;

        // 4. Encargo vigente de ESTA operación, de ESTE Operador CAE, sobre ESTE
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

        // 5. Perfil de Propiedad en el Tenant de origen, de Identity.
        var perfil = await perfilDePropiedadEnOrigen.ObtenerAsync(usuarioId, tenantOrigenId, cancellationToken);
        if (perfil is null || !RolesDePropiedad.Contains(perfil))
            return soloCartera;

        // 6. La sesión tiene que decir lo mismo: la claim no eleva, solo impide.
        if (!string.Equals(rolDeSesionEnOrigen, perfil, StringComparison.Ordinal))
            return soloCartera;

        // 7. El perfil de origen, con el encargo que lo ampara. 8. Pleno solo si el
        // Tenant propietario no tiene Administrador propio, ni activo ni pendiente de activar.
        var pleno = !await tenantConAdministradorPropio.TieneAdministradorPropioAsync(tenantId, cancellationToken);
        return new RolEfectivoPorOperacion(perfil, encargoId, pleno);
    }
}
