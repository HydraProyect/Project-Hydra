using CaeManager.Application.Common;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Concede, en las siembras de demo y de pruebas, la Asignación de Cartera del <b>Tenant entero</b>
/// a los Gestores CAE sembrados (ADR-011 § 2.7, enmienda 2026-09-23 y D-7, 2026-10-02).
///
/// <para>
/// Antes el reparto lo derivaba, en cada arranque, <c>AsignacionesOperativasBackfillSeeder</c> de
/// <c>Empresa.EjecutivoUsuarioId</c>: ser la referencia de un Cliente empresarial concedía su
/// cartera. Eso está retirado —la referencia no concede alcance—, así que cada siembra que crea un
/// Gestor CAE con Clientes empresariales lo dice aquí, por el mismo
/// <see cref="AsignacionesOperativasWriter"/> que usa la aplicación. Es un acto explícito de la
/// siembra, no una derivación.
/// </para>
/// </summary>
internal static class CarterasDeSiembra
{
    /// <summary>
    /// Asegura la operación raíz del Tenant y una cartera universal interna por cada Gestor CAE
    /// (usuarios del propio Tenant). Idempotente, y guarda.
    /// </summary>
    public static async Task AsegurarTenantEnteroAsync(
        CaeManagerDbContext dbContext, Guid tenantId, IEnumerable<Guid> gestorIds, CancellationToken cancellationToken)
    {
        var gestores = gestorIds.Distinct().ToList();
        if (gestores.Count == 0) return;

        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            var writer = new AsignacionesOperativasWriter(
                dbContext, new TenantActualAmbiental { TenantId = tenantId }, new EscenariosDireccionDemoSeeder.ActorDeSiembra());

            var creadoEnUtc = await dbContext.Tenants
                .Where(t => t.Id == tenantId)
                .Select(t => t.CreadoEnUtc)
                .FirstAsync(cancellationToken);
            await writer.AsegurarOperacionRaizAsync(tenantId, creadoEnUtc, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            foreach (var gestorId in gestores)
                await writer.AsegurarCarteraTenantEnteroAsync(tenantId, gestorId, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
