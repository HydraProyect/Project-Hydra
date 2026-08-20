using CaeManager.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CaeManager.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Renueva <see cref="IVersionable.Version"/> en cada modificación. Es lo que
/// hace útil el token: EF pone el valor <b>original</b> en el WHERE del
/// UPDATE y el nuevo en el SET, de modo que si otra transacción guardó
/// entremedias, el UPDATE afecta a cero filas y EF lanza
/// <c>DbUpdateConcurrencyException</c> en vez de sobrescribir en silencio.
///
/// Sin este interceptor la columna existiría pero nunca cambiaría, y el token
/// no detectaría nada — el fallo clásico de la concurrencia optimista mal
/// montada.
///
/// Mismo criterio que <c>TenantSelladoInterceptor</c>: se sobrescriben las dos
/// versiones, síncrona y asíncrona, para que un guardado síncrono futuro no se
/// salte la protección por omisión.
/// </summary>
public class ConcurrenciaOptimistaInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is DbContext context)
            RenovarVersiones(context);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is DbContext context)
            RenovarVersiones(context);

        return base.SavingChanges(eventData, result);
    }

    private static void RenovarVersiones(DbContext context)
    {
        // IVersionable y no EntidadBase: los catálogos globales de asignación
        // operativa llevan token pero no heredan de EntidadBase (no pertenecen a
        // un tenant). Enumerar por la clase base los habría dejado con la
        // columna presente y el valor congelado — un token inerte, que es peor
        // que no tenerlo porque aparenta protección.
        foreach (var entrada in context.ChangeTracker.Entries<IVersionable>())
        {
            // Solo Modified: en Added el valor del constructor ya sirve, y en
            // Deleted cambiarlo alteraría el WHERE que debe comprobar el
            // valor original.
            if (entrada.State != EntityState.Modified) continue;

            entrada.Property(nameof(IVersionable.Version)).CurrentValue = Guid.NewGuid();
        }
    }
}
