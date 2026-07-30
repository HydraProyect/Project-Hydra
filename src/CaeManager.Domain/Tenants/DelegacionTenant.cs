using CaeManager.Domain.Common;

namespace CaeManager.Domain.Tenants;

/// <summary>
/// Delegación de acceso de una Consultora (Tenant sin datos operativos
/// propios) sobre un Cliente Delegante (otro Tenant, dueño de sus datos) —
/// ver ADR-004-delegacion-consultoras-cae.md § 5.3. Cada fila con
/// <see cref="Activa"/> es, en el vocabulario de negocio, un
/// <c>Delegated Workspace</c>.
///
/// Extiende <see cref="Entity"/>, no <see cref="EntidadConTenant"/>: es un
/// catálogo global de autorización, no un dato que pertenezca a un único
/// tenant — igual tratamiento que <see cref="Tenant"/> (sin
/// <c>HasQueryFilter</c>, ver CaeManagerDbContext). Nunca tiene FK hacia
/// Empresa/Centro/Trabajador/Documento — es la representación literal de
/// "delegación de acceso, no de propiedad" (ADR-004 § 2).
///
/// "Desactivar, nunca borrar": el Escenario 4 (internalización, ADR-004 § 2)
/// es <see cref="Desactivar"/>, no un soft delete — conserva el histórico de
/// qué Consultora operó sobre qué tenant y cuándo.
/// </summary>
public class DelegacionTenant : Entity
{
    public Guid TenantConsultoraId { get; private set; }
    public Guid TenantClienteId { get; private set; }
    public bool Activa { get; private set; }
    public DateTime CreadoEnUtc { get; private set; } = DateTime.UtcNow;

    private DelegacionTenant()
    {
        // Requerido por EF Core.
    }

    public DelegacionTenant(Guid tenantConsultoraId, Guid tenantClienteId)
    {
        if (tenantConsultoraId == Guid.Empty)
            throw new ArgumentException("La delegación debe tener una Consultora.", nameof(tenantConsultoraId));
        if (tenantClienteId == Guid.Empty)
            throw new ArgumentException("La delegación debe tener un Cliente Delegante.", nameof(tenantClienteId));
        if (tenantConsultoraId == tenantClienteId)
            throw new ArgumentException("Un tenant no puede delegarse acceso a sí mismo.", nameof(tenantClienteId));

        TenantConsultoraId = tenantConsultoraId;
        TenantClienteId = tenantClienteId;
        Activa = true;
    }

    public void Desactivar() => Activa = false;

    public void Reactivar() => Activa = true;
}
