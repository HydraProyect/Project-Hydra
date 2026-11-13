using CaeManager.Domain.Cumplimiento;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

/// <summary>Filtro de Tenant global (CaeManagerDbContext) y RLS: todo lo que lee es del Tenant actual.</summary>
public class AceptacionContractualTenantRepository(CaeManagerDbContext dbContext) : IAceptacionContractualTenantRepository
{
    public void Agregar(AceptacionContractualTenant aceptacion) => dbContext.AceptacionesContractualesTenant.Add(aceptacion);

    public void AgregarAcuse(AcuseAceptacionContractualTenant acuse) => dbContext.AcusesAceptacionContractualTenant.Add(acuse);

    /// <summary>
    /// También cuenta las añadidas y aún sin guardar: si el Command del alta repite su delegado con el mismo
    /// contexto tras un fallo de guardado, las del primer intento siguen en el ChangeTracker y la consulta a la
    /// base de datos no las ve.
    /// </summary>
    public async Task<bool> ExistenDeSolicitudAltaAsync(Guid solicitudAltaId, CancellationToken cancellationToken = default) =>
        dbContext.AceptacionesContractualesTenant.Local.Any(a => a.SolicitudAltaId == solicitudAltaId)
        || await dbContext.AceptacionesContractualesTenant.AnyAsync(a => a.SolicitudAltaId == solicitudAltaId, cancellationToken);

    public async Task<IReadOnlyList<AceptacionContractualTenant>> ListarAsync(CancellationToken cancellationToken = default) =>
        await dbContext.AceptacionesContractualesTenant.AsNoTracking()
            .OrderByDescending(a => a.AceptadaEnUtc)
            .ThenByDescending(a => a.RegistradaEnUtc)
            .ThenBy(a => a.Documento)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, DateTime>> UltimoAcusePorAceptacionAsync(CancellationToken cancellationToken = default) =>
        await dbContext.AcusesAceptacionContractualTenant.AsNoTracking()
            .GroupBy(a => a.AceptacionContractualTenantId)
            .Select(g => new { g.Key, Ultimo = g.Max(a => a.EnviadoEnUtc) })
            .ToDictionaryAsync(x => x.Key, x => x.Ultimo, cancellationToken);
}
