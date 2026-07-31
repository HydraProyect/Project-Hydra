using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CaeManager.Infrastructure.MultiTenancy;

/// <summary>
/// Sella <c>TenantId</c> en toda entidad nueva desde <see cref="ITenantActual"/>
/// (ver docs/MULTITENANCY.md § 4.3) y rechaza cualquier modificación o
/// eliminación de una entidad que pertenezca a otro tenant — defensa en
/// profundidad además del filtro global de lectura (ver
/// <c>CaeManagerDbContext.OnModelCreating</c>), para el caso de una entidad
/// cargada por una vía que no pasó por el filtro (p. ej. un
/// <c>IgnoreQueryFilters()</c> justificado y revisado). Los Commands nunca
/// asignan <c>TenantId</c> directamente — es exclusivo de este interceptor,
/// mismo principio arquitectónico que <c>AuditoriaInterceptor</c> para los
/// campos de auditoría.
/// </summary>
public class TenantSelladoInterceptor(ITenantActual tenantActual) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is DbContext context)
            SellarYValidar(context);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>
    /// La versión síncrona también sella (hallazgo N-15 de
    /// INFORME-AUDITORIA-2.md). Hoy no hay ningún <c>SaveChanges()</c>
    /// síncrono en el código, así que sobrescribir solo la asíncrona era
    /// inocuo — pero el día que aparezca uno, saltarse el sellado no daría
    /// ningún error: guardaría la fila con <c>TenantId</c> vacío o permitiría
    /// modificar la de otro tenant, en silencio. Un agujero que se abre por
    /// omisión no debería depender de que nadie escriba una línea corriente.
    /// </summary>
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is DbContext context)
            SellarYValidar(context);

        return base.SavingChanges(eventData, result);
    }

    private void SellarYValidar(DbContext context)
    {
        var tenantId = tenantActual.TenantId;

        foreach (var entrada in context.ChangeTracker.Entries<EntidadConTenant>())
        {
            switch (entrada.State)
            {
                case EntityState.Added:
                    if (tenantId is null)
                        throw new InvalidOperationException(
                            $"No se puede crear una entidad de tipo {entrada.Entity.GetType().Name} sin un tenant resuelto (ver ITenantActual).");

                    entrada.Property(nameof(EntidadConTenant.TenantId)).CurrentValue = tenantId.Value;
                    break;

                case EntityState.Modified:
                case EntityState.Deleted:
                    var tenantOriginal = (Guid)entrada.Property(nameof(EntidadConTenant.TenantId)).OriginalValue!;
                    if (tenantOriginal != tenantId)
                        throw new InvalidOperationException(
                            $"No se puede modificar una entidad de tipo {entrada.Entity.GetType().Name} perteneciente a otro tenant.");
                    break;
            }
        }
    }
}
