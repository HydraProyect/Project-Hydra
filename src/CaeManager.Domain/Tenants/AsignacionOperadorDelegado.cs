using CaeManager.Domain.Common;

namespace CaeManager.Domain.Tenants;

/// <summary>
/// Un Operador Delegado (usuario de la Consultora) autorizado a operar sobre
/// un <see cref="DelegacionTenant"/> concreto, con un rol específico para ese
/// Delegated Workspace — ver ADR-004-delegacion-consultoras-cae.md § 5.3. Un
/// mismo usuario puede tener roles distintos en delegaciones distintas (p.
/// ej. GestorCae en un cliente, Consulta en otro).
///
/// Extiende <see cref="Entity"/>, no <see cref="EntidadConTenant"/>: mismo
/// catálogo global que <see cref="DelegacionTenant"/>.
///
/// <see cref="UsuarioId"/> es un Guid suelto sin navegación EF hacia
/// <c>ApplicationUser</c> — Identity vive en Infrastructure, no accesible
/// desde Domain (mismo patrón que <c>ApplicationUser.CoordinadorUsuarioId</c>).
/// <see cref="Rol"/> es un código de rol en texto plano por el mismo motivo:
/// Application/Domain no referencian <c>CaeManager.Infrastructure.Identity.Roles</c>
/// para no invertir la dependencia entre capas (ver
/// AutorizacionEscrituraBehavior) — la validación de que sea un código de rol
/// conocido vive en el validador del Command, no aquí.
/// </summary>
public class AsignacionOperadorDelegado : Entity
{
    public Guid DelegacionTenantId { get; private set; }
    public Guid UsuarioId { get; private set; }
    public string Rol { get; private set; } = string.Empty;
    public DateTime CreadoEnUtc { get; private set; } = DateTime.UtcNow;

    private AsignacionOperadorDelegado()
    {
        // Requerido por EF Core.
    }

    public AsignacionOperadorDelegado(Guid delegacionTenantId, Guid usuarioId, string rol)
    {
        if (delegacionTenantId == Guid.Empty)
            throw new ArgumentException("La asignación debe pertenecer a una delegación.", nameof(delegacionTenantId));
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("La asignación debe tener un Operador Delegado.", nameof(usuarioId));
        if (string.IsNullOrWhiteSpace(rol))
            throw new ArgumentException("La asignación debe tener un rol.", nameof(rol));

        DelegacionTenantId = delegacionTenantId;
        UsuarioId = usuarioId;
        Rol = rol.Trim();
    }
}
