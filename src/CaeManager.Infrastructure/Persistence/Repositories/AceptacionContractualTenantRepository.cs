using CaeManager.Domain.Cumplimiento;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

/// <summary>Filtro de Tenant global (CaeManagerDbContext) y RLS: todo lo que lee es del Tenant actual.</summary>
public class AceptacionContractualTenantRepository(CaeManagerDbContext dbContext) : IAceptacionContractualTenantRepository
{
    public void Agregar(AceptacionContractualTenant aceptacion) => dbContext.AceptacionesContractualesTenant.Add(aceptacion);

    public void AgregarAcuse(AcuseAceptacionContractualTenant acuse) => dbContext.AcusesAceptacionContractualTenant.Add(acuse);

    public bool HayTransaccionAbierta =>
        dbContext.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null;

    public bool HayCambiosPendientes => dbContext.ChangeTracker.HasChanges();

    /// <summary>
    /// También cuenta las añadidas y aún sin guardar: si el Command del alta repite su delegado con el mismo
    /// contexto tras un fallo de guardado, las del primer intento siguen en el ChangeTracker y la consulta a la
    /// base de datos no las ve. En el ChangeTracker no se puede filtrar por Tenant (una fila pendiente se sella
    /// al guardar), así que se filtra por la cuenta: las de otra cuenta, por ejemplo de un intento que creó
    /// otro Tenant, no dan por registrada esta, y si se guardaran junto a las nuevas chocarían con el índice
    /// único en vez de pasar en silencio. La consulta a la base de datos va acotada al Tenant por RLS y también
    /// a la cuenta: una petición para otra cuenta no recibe un éxito que oculte el conflicto, sigue a las
    /// comprobaciones de autoridad y de correo, y el índice único impide que deje una segunda atribución.
    /// </summary>
    public async Task<bool> ExistenDeSolicitudAltaAsync(Guid solicitudAltaId, Guid usuarioId, CancellationToken cancellationToken = default) =>
        dbContext.AceptacionesContractualesTenant.Local.Any(a => a.SolicitudAltaId == solicitudAltaId && a.UsuarioId == usuarioId)
        || await dbContext.AceptacionesContractualesTenant.AnyAsync(
            a => a.SolicitudAltaId == solicitudAltaId && a.UsuarioId == usuarioId, cancellationToken);

    public async Task<IReadOnlyList<AceptacionContractualTenant>> ListarAsync(CancellationToken cancellationToken = default) =>
        await dbContext.AceptacionesContractualesTenant.AsNoTracking()
            .OrderByDescending(a => a.AceptadaEnUtc)
            .ThenByDescending(a => a.RegistradaEnUtc)
            .ThenBy(a => a.Documento)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, DateTime>> UltimoAcusePorAceptacionAsync(CancellationToken cancellationToken = default) =>
        await dbContext.AcusesAceptacionContractualTenant.AsNoTracking()
            .Where(a => a.Fase == FaseAcuseAceptacionContractual.Enviado)
            .GroupBy(a => a.AceptacionContractualTenantId)
            .Select(g => new { g.Key, Ultimo = g.Max(a => a.RegistradoEnUtc) })
            .ToDictionaryAsync(x => x.Key, x => x.Ultimo, cancellationToken);
}
