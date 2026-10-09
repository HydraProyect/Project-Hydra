using CaeManager.Application.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// La retirada de la siembra del piloto Outbound: el camino de vuelta de
/// <see cref="PilotoOutboundSeeder"/>. Vive aparte del sembrador porque usa dos
/// identidades —valida con la no privilegiada y borra con la de bootstrap—, y un
/// sembrador solo escribe con la de tráfico, bajo RLS.
///
/// <para>
/// <b>Qué no borra.</b> Los directorios de cada Tenant en el almacén (quedan vacíos),
/// los temporales <c>.tmp-*</c> que una escritura interrumpida haya dejado en ellos
/// —no tienen fila que los nombre—, y las filas de auditoría «Eliminado» que el
/// propio borrado genera, selladas con el Tenant que acaba de desaparecer.
/// </para>
/// </summary>
public static class PilotoOutboundRetirada
{
    public const string Argumento = "--retirar-piloto-outbound";

    /// <summary>
    /// La entrada del modo de línea de órdenes: exige la misma confirmación de entorno
    /// que la siembra administrativa (<see cref="PilotoOutboundAdministrativa.ClaveConfirmarEntorno"/>)
    /// antes de leer nada, y después retira el lote con <see cref="RetirarLoteAsync"/>.
    /// </summary>
    /// <param name="alRetirar">Se invoca en cuanto se ha borrado cada Tenant, no al final del lote: si la retirada falla a mitad, ya se ha dicho qué se borró.</param>
    public static Task<IReadOnlyList<RetiradaTenantDemoService.ResultadoRetirada>> RetirarLoteConfirmadoAsync(
        IConfiguration configuration, IHostEnvironment entorno,
        CaeManagerDbContext dbContextNoPrivilegiado, Func<CaeManagerDbContext> crearContextoDeBootstrap,
        IFileStorageService almacen, ILogger logger, Action<RetiradaTenantDemoService.ResultadoRetirada>? alRetirar = null,
        CancellationToken cancellationToken = default)
    {
        PilotoOutboundAdministrativa.ExigirEntornoConfirmado(
            configuration[PilotoOutboundAdministrativa.ClaveConfirmarEntorno], entorno);

        return RetirarLoteAsync(dbContextNoPrivilegiado, crearContextoDeBootstrap, almacen, logger, cancellationToken, alRetirar);
    }

    /// <summary>
    /// Retira el lote completo, Tenants propietarios primero y el Operador CAE
    /// externo al final. Valida TODOS con identidad no privilegiada (nombre y
    /// marcador) antes de elevar, y no retira ninguno si alguno no lo supera. Antes
    /// de borrar las filas de cada Tenant elimina del almacén los PDF de sus
    /// documentos, dentro de su ámbito. Los que no existen se saltan (repetible).
    /// No es atómica entre Tenants: cada uno se borra y se confirma por separado, y
    /// <paramref name="alRetirar"/> lo anuncia en ese momento. Sin la confirmación de
    /// entorno: el modo de línea de órdenes entra por <see cref="RetirarLoteConfirmadoAsync"/>.
    /// </summary>
    internal static async Task<IReadOnlyList<RetiradaTenantDemoService.ResultadoRetirada>> RetirarLoteAsync(
        CaeManagerDbContext dbContextNoPrivilegiado, Func<CaeManagerDbContext> crearContextoDeBootstrap,
        IFileStorageService almacen, ILogger logger, CancellationToken cancellationToken = default,
        Action<RetiradaTenantDemoService.ResultadoRetirada>? alRetirar = null)
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

            var resultado = await RetiradaTenantDemoService.RetirarAsync(dbContextRetirada, tenant, logger, cancellationToken);
            resultados.Add(resultado);
            alRetirar?.Invoke(resultado);
        }

        return resultados;
    }
}
