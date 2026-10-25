using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CaeManager.Infrastructure.Persistence;

public static class ConcurrenciaContexto
{
    // Primero se ejecuta la consulta normal, con sus filtros. Solo se refresca una instancia
    // que el circuito ya tenía: EF no reemplaza sus valores al materializar otra consulta.
    public static async Task<T?> ObtenerActualAsync<T>(
        this DbContext contexto, IQueryable<T> consulta, CancellationToken cancellationToken = default)
        where T : class, IVersionable
    {
        var rastreadas = contexto.ChangeTracker.Entries<T>()
            .Select(e => (object)e.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        var entidad = await consulta.FirstOrDefaultAsync(cancellationToken);
        if (entidad is null || !rastreadas.Contains(entidad))
            return entidad;

        var entrada = contexto.Entry(entidad);
        await entrada.ReloadAsync(cancellationToken);
        return entrada.State == EntityState.Detached ? null : entidad;
    }

    // La excepción se propaga a ConcurrenciaBehavior. La edición perdida y la auditoría
    // del guardado fallido no pueden quedar pendientes para otro SaveChanges del circuito.
    public static void DescartarEdicionPerdida(DbContext contexto, IEnumerable<EntityEntry> entradas)
    {
        var perdidas = entradas.ToList();
        var auditorias = contexto.ChangeTracker.Entries<RegistroAuditoria>()
            .Where(e => e.State == EntityState.Added).ToList();
        foreach (var entrada in perdidas)
            entrada.State = EntityState.Detached;
        foreach (var entrada in auditorias)
            entrada.State = EntityState.Detached;
    }
}
