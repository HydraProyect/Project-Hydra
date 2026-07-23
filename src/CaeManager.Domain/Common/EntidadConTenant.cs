namespace CaeManager.Domain.Common;

/// <summary>
/// Entidad perteneciente a un Tenant (ver docs/MULTITENANCY.md). Separada de
/// <see cref="Entity"/> (no de <see cref="EntidadBase"/>) porque no todas las
/// entidades multi-tenant tienen soft delete (las tablas de unión y catálogos
/// por-tenant no lo tienen), pero prácticamente todas — salvo <c>Tenant</c>
/// misma, la única entidad que queda deliberadamente fuera de esta jerarquía —
/// pertenecen a un tenant.
///
/// <see cref="TenantId"/> es nullable durante la Etapa 1 de
/// <c>PLAN-MIGRACION-MULTITENANT.md</c> (esquema aditivo, sin filtro global
/// todavía). Pasa a NOT NULL en la Etapa 3 (cierre), momento en el que
/// también se sella exclusivamente desde el interceptor de <c>SaveChanges</c>
/// — de ahí el <c>private set</c> ya desde ahora: ningún Command debe poder
/// asignarlo directamente.
/// </summary>
public abstract class EntidadConTenant : Entity
{
    public Guid? TenantId { get; private set; }
}
