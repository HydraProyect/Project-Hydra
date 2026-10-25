using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CaeManager.Infrastructure.Persistence;

/// <summary>
/// En Blazor Server el <c>DbContext</c> vive lo que el circuito (ver <c>IDescarteCambiosPendientes</c>):
/// una entidad <see cref="IVersionable"/> que el circuito ya cargó o guardó sigue rastreada con los
/// valores de entonces. Generaliza lo que la PR #1661 hizo para la Plataforma CAE del Cliente empresarial.
/// </summary>
public static class ConcurrenciaContexto
{
    /// <summary>
    /// Ejecuta la consulta normal, con sus filtros, y si devuelve una instancia que el circuito ya tenía
    /// rastreada sin cambios la relee: EF no reemplaza sus valores al materializar otra consulta, y el
    /// comando compararía la Version de pantalla con un token viejo (conflicto falso permanente tras la
    /// edición de otra persona). Una instancia con cambios pendientes no se relee: son de la operación en
    /// curso y la relectura los perdería en silencio. Devuelve null si la fila desapareció entre la
    /// consulta y la relectura.
    /// </summary>
    public static async Task<T?> ObtenerActualAsync<T>(
        this DbContext contexto, IQueryable<T> consulta, CancellationToken cancellationToken = default)
        where T : class, IVersionable
    {
        var rastreadas = contexto.ChangeTracker.Entries<T>()
            .Where(e => e.State == EntityState.Unchanged)
            .Select(e => (object)e.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        var entidad = await consulta.FirstOrDefaultAsync(cancellationToken);
        if (entidad is null || !rastreadas.Contains(entidad))
            return entidad;

        var entrada = contexto.Entry(entidad);
        await entrada.ReloadAsync(cancellationToken);
        return entrada.State == EntityState.Detached ? null : entidad;
    }

    /// <summary>
    /// Tras un <see cref="DbUpdateConcurrencyException"/> el lote entero se revirtió en la base, pero EF
    /// deja sus entradas como estaban: el siguiente SaveChanges del circuito reintentaría la edición perdida,
    /// sus cambios relacionados (por ejemplo, las Relaciones Empresariales de una edición de Empresa) y la
    /// auditoría del intento. Se desacopla todo lo pendiente, incluido lo que los interceptores añadieron
    /// durante el guardado; las entidades sin cambios se conservan. La excepción sigue su camino hasta
    /// ConcurrenciaBehavior.
    /// </summary>
    public static void DescartarLoteFallido(DbContext contexto, DbUpdateConcurrencyException ex) =>
        DescartarEdicionPerdida(contexto, contexto.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Concat(ex.Entries));

    /// <summary>
    /// Desacopla las entradas indicadas y las auditorías aún sin guardar.
    /// </summary>
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
