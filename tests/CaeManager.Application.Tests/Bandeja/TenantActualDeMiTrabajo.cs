using CaeManager.Application.Common;

namespace CaeManager.Application.Tests.Bandeja;

/// <summary>
/// <see cref="ITenantActual"/> fijo para construir el handler de Mi trabajo en las pruebas unitarias. Sin Sesión
/// Privilegiada seleccionada el handler no lo lee; con ella, es el Tenant objetivo (issue #2351).
/// </summary>
internal sealed class TenantActualDeMiTrabajo(Guid? tenantId = null) : ITenantActual
{
    public Guid? TenantId => tenantId;
}
