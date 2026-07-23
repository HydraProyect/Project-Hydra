using CaeManager.Application.Common;

namespace CaeManager.Infrastructure.MultiTenancy;

/// <summary>
/// Implementación de <see cref="ITenantActual"/> para contextos sin sesión
/// de usuario: procesos de fondo (ver PLAN-MIGRACION-MULTITENANT.md § 4.7,
/// "ámbito de tenant explícito para jobs"), migraciones y tests de
/// integración. A diferencia de la implementación Web (que resuelve el
/// claim de la sesión), aquí el tenant se asigna explícitamente antes de
/// operar — un job que procese varios tenants abre un ámbito por cada uno
/// (instancia nueva o reasignación de <see cref="TenantId"/>), nunca
/// comparte un mismo valor entre tenants distintos.
/// </summary>
public class TenantActualAmbiental : ITenantActual
{
    public Guid? TenantId { get; set; }
}
