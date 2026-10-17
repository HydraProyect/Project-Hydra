using CaeManager.Application.Common;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Seed;

public static class PlantillasRespuestaSeeder
{
    /// <summary>Solo bootstrap administrativo, igual que el backfill de asignaciones operativas.</summary>
    public static async Task SeedAsync(CaeManagerDbContext contextoBootstrap, CancellationToken cancellationToken = default)
    {
        var tenantIds = await contextoBootstrap.Tenants.Select(t => t.Id).ToListAsync(cancellationToken);
        foreach (var tenantId in tenantIds)
        {
            using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
            var plantillas = PlantillasRespuestaPredefinidas.Crear(tenantId);
            var ids = plantillas.Select(p => p.Id).ToList();
            // Incluye soft delete para respetar la eliminación humana. Filtro explícito de Tenant propietario.
            var existentes = await contextoBootstrap.MacrosRespuesta.IgnoreQueryFilters()
                .Where(m => m.TenantId == tenantId && ids.Contains(m.Id))
                .Select(m => m.Id).ToListAsync(cancellationToken);
            foreach (var plantilla in plantillas.Where(p => !existentes.Contains(p.Id)))
            {
                contextoBootstrap.MacrosRespuesta.Add(plantilla);
                contextoBootstrap.Entry(plantilla).Property(p => p.TenantId).CurrentValue = tenantId;
            }
            await contextoBootstrap.SaveChangesAsync(cancellationToken);
        }
    }
}
