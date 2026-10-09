using CaeManager.Application.Common;

namespace CaeManager.Application.Tests.Configuracion;

/// <summary>
/// Tenant activo de la sesión para los tests de filtros guardados: fijo, o
/// <c>null</c> para "sin Tenant resuelto".
/// </summary>
public sealed class TenantActualFijo(Guid? tenantId) : ITenantActual
{
    public Guid? TenantId { get; } = tenantId;
}
