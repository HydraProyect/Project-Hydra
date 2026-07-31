using CaeManager.Application.Common;
using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Asegura que todo tenant tiene su delegación de soporte aprovisionada —
/// <b>apagada</b>— hacia el tenant de plataforma.
///
/// Se ejecuta al arrancar y es idempotente. Se resuelve así, y no como parte
/// de un alta de tenants, porque hoy no existe tal alta: los tenants se crean
/// por seeder, y el aprovisionamiento sigue siendo una condición de salida
/// abierta de ADR-003. Cuando exista, este mismo servicio se invoca desde
/// ahí y este barrido deja de encontrar nada que hacer.
///
/// Aprovisionar no concede nada: la delegación nace inactiva y abrirla exige
/// motivo y ventana (ver <c>DelegacionTenant.ActivarParaSoporte</c>). Lo que
/// se evita es tener que montar la relación con prisa cuando entra una queja.
/// </summary>
public static class DelegacionesSoporteSeeder
{
    public static async Task SeedAsync(
        CaeManagerDbContext dbContext, ILogger logger, CancellationToken cancellationToken = default)
    {
        var tenantPlataforma = await dbContext.Tenants
            .FirstOrDefaultAsync(t => t.EsPlataforma, cancellationToken);

        if (tenantPlataforma is null)
        {
            // Sin tenant de plataforma marcado no hay a quién delegar. No es
            // un error: un despliegue puede no querer acceso de soporte.
            logger.LogInformation("Ningún tenant está marcado como plataforma — no se aprovisionan delegaciones de soporte.");
            return;
        }

        var tenantIds = await dbContext.Tenants
            .Where(t => !t.EsPlataforma)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        if (tenantIds.Count == 0) return;

        var yaAprovisionados = await dbContext.DelegacionesTenant
            .Where(d => d.TenantConsultoraId == tenantPlataforma.Id && d.Proposito == PropositoDelegacion.Soporte)
            .Select(d => d.TenantClienteId)
            .ToListAsync(cancellationToken);

        var pendientes = tenantIds.Except(yaAprovisionados).ToList();
        if (pendientes.Count == 0) return;

        foreach (var tenantId in pendientes)
            dbContext.DelegacionesTenant.Add(DelegacionTenant.ParaSoporte(tenantPlataforma.Id, tenantId));

        // DelegacionTenant es catálogo global (no extiende EntidadConTenant),
        // pero el interceptor de sellado exige un tenant resuelto para
        // cualquier guardado — al arrancar no hay sesión, así que se establece
        // explícitamente, mismo patrón que el resto de seeders.
        using (AmbitoTenantExplicito.Establecer(tenantPlataforma.Id))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation(
            "Aprovisionadas {Cantidad} delegaciones de soporte (inactivas) hacia el tenant de plataforma.", pendientes.Count);
    }
}
