namespace CaeManager.Domain.Common;

/// <summary>
/// Entidad perteneciente a un Tenant (ver docs/MULTITENANCY.md). Separada de
/// <see cref="Entity"/> (no de <see cref="EntidadBase"/>) porque no todas las
/// entidades multi-tenant tienen soft delete (las tablas de unión y catálogos
/// por-tenant no lo tienen), pero prácticamente todas — salvo <c>Tenant</c>
/// misma, la única entidad que queda deliberadamente fuera de esta jerarquía —
/// pertenecen a un tenant.
///
/// <see cref="TenantId"/> se sella exclusivamente desde
/// <c>TenantSelladoInterceptor</c> (Infrastructure) al guardar — ningún
/// Command lo asigna directamente, de ahí el <c>private set</c>. NOT NULL
/// desde la Etapa 3 de <c>PLAN-MIGRACION-MULTITENANT.md</c> (cierre); antes
/// de esa etapa la columna física fue nullable (esquema aditivo, Etapa 1),
/// pero el tipo del dominio ya lo modela como requerido.
/// </summary>
public abstract class EntidadConTenant : Entity
{
    public Guid TenantId { get; private set; }
}
