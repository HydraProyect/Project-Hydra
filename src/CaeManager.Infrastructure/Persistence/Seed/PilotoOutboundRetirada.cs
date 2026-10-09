using CaeManager.Application.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// La retirada de la siembra del piloto Outbound: el camino de vuelta de
/// <see cref="PilotoOutboundSeeder"/>. Vive aparte del sembrador porque usa dos
/// identidades —valida con la no privilegiada y borra con la de bootstrap—, y un
/// sembrador solo escribe con la de tráfico, bajo RLS.
/// </summary>
public static class PilotoOutboundRetirada
{
    public const string Argumento = "--retirar-piloto-outbound";

    /// <summary>
    /// Retira el lote completo, Tenants propietarios primero y el Operador CAE
    /// externo al final. Valida TODOS con identidad no privilegiada (nombre y
    /// marcador) antes de elevar, y no retira ninguno si alguno no lo supera. Antes
    /// de borrar las filas de cada Tenant elimina del almacén los PDF de sus
    /// documentos, dentro de su ámbito. Los que no existen se saltan (repetible).
    /// </summary>
    public static async Task<IReadOnlyList<RetiradaTenantDemoService.ResultadoRetirada>> RetirarLoteAsync(
        CaeManagerDbContext dbContextNoPrivilegiado, Func<CaeManagerDbContext> crearContextoDeBootstrap,
        IFileStorageService almacen, ILogger logger, CancellationToken cancellationToken = default)
    {
        var nombres = CatalogoPilotoOutbound.NombresTenants.ToList();
        var existentes = await dbContextNoPrivilegiado.Tenants
            .Where(t => nombres.Contains(t.Nombre)).Select(t => new { t.Id, t.Nombre }).ToListAsync(cancellationToken);

        var validados = new List<Tenant>();
        foreach (var nombre in nombres)
        {
            if (existentes.SingleOrDefault(t => t.Nombre == nombre) is not { } existente) continue;

            validados.Add(await RetiradaTenantDemoService.ValidarTenantRetirableAsync(
                dbContextNoPrivilegiado, existente.Id, cancellationToken));
        }

        var resultados = new List<RetiradaTenantDemoService.ResultadoRetirada>();
        if (validados.Count == 0) return resultados;

        await using var dbContextRetirada = crearContextoDeBootstrap();
        foreach (var tenant in validados)
        {
            using (AmbitoTenantExplicito.Establecer(tenant.Id))
            {
                var claves = await dbContextNoPrivilegiado.Documentos
                    .Where(d => d.ArchivoUrl != null).Select(d => d.ArchivoUrl!).ToListAsync(cancellationToken);
                foreach (var clave in claves)
                    await almacen.EliminarAsync(clave, cancellationToken);
            }

            resultados.Add(await RetiradaTenantDemoService.RetirarAsync(dbContextRetirada, tenant, logger, cancellationToken));
        }

        return resultados;
    }
}
