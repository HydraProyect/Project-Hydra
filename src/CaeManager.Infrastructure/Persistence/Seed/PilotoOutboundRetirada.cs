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
/// —no tienen fila que los nombre—, los ficheros cuya clave vive en una columna que
/// <see cref="ClavesDeFicherosAsync"/> no lee, y las filas de auditoría «Eliminado» que el
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
    /// de borrar las filas de cada Tenant elimina del almacén, dentro de su ámbito,
    /// los ficheros que esas filas nombran (<see cref="ClavesDeFicherosAsync"/>).
    /// Los Tenants que no existen se saltan (repetible).
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
            // Ficheros ANTES que filas, a propósito: las claves solo están en las filas. Si se borraran
            // primero las filas y fallara el borrado de un fichero, ya no habría de dónde leer cuáles quedan.
            using (AmbitoTenantExplicito.Establecer(tenant.Id))
            {
                foreach (var clave in await ClavesDeFicherosAsync(dbContextNoPrivilegiado, tenant, cancellationToken))
                    await almacen.EliminarAsync(clave, cancellationToken);
            }

            var resultado = await RetiradaTenantDemoService.RetirarAsync(dbContextRetirada, tenant, logger, cancellationToken);
            resultados.Add(resultado);
            alRetirar?.Invoke(resultado);
        }

        return resultados;
    }

    /// <summary>
    /// Las claves del almacén que nombran las filas de un Tenant: los PDF de sus documentos
    /// —<b>también los descartados</b>, cuyo fichero sigue en disco—, las plantillas en blanco
    /// de sus Centros, los adjuntos de sus mensajes, los originales de sus plantillas y su logo.
    ///
    /// <para>
    /// Solo la consulta de documentos quita los filtros globales: es la única de estas tablas con
    /// borrado lógico, y su filtro escondería los descartados. Ese mismo filtro es el que acota
    /// por Tenant, así que al quitarlo el predicado del Tenant va escrito a mano; las demás lo
    /// llevan también, aunque su filtro global ya lo aplique, para que ninguna dependa de él.
    /// RLS (la lectura va con la identidad no privilegiada, dentro del ámbito del Tenant) y la
    /// resolución de rutas del almacén siguen siendo la barrera; la consulta no descansa solo en ellas.
    /// </para>
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ClavesDeFicherosAsync(
        CaeManagerDbContext dbContextNoPrivilegiado, Tenant tenant, CancellationToken cancellationToken)
    {
        var tenantId = tenant.Id;
        var deDocumentos = await dbContextNoPrivilegiado.Documentos.IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId).Select(d => d.ArchivoUrl).ToListAsync(cancellationToken);

        List<string?> claves =
        [
            tenant.LogoArchivoClave,
            .. deDocumentos,
            .. await dbContextNoPrivilegiado.TiposDocumentoCentros
                .Where(t => t.TenantId == tenantId).Select(t => t.ArchivoUrl).ToListAsync(cancellationToken),
            .. await dbContextNoPrivilegiado.AdjuntosMensaje
                .Where(a => a.TenantId == tenantId).Select(a => (string?)a.ArchivoUrl).ToListAsync(cancellationToken),
            .. await dbContextNoPrivilegiado.PlantillasDocumentoVersion
                .Where(v => v.TenantId == tenantId).Select(v => v.ArchivoOriginalUrl).ToListAsync(cancellationToken),
        ];

        return [.. claves.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Lo que el modo escribe por la salida de error cuando la retirada no termina, sea un
    /// rechazo previo a todo borrado o un fallo a mitad: el tipo y el mensaje de la excepción
    /// (nunca su traza ni sus datos) y cómo seguir.
    /// </summary>
    public static string MensajeDeInterrupcion(Exception excepcion) =>
        $"Retirada del piloto Outbound interrumpida: {PilotoOutboundAdministrativa.Motivo(excepcion)}{Environment.NewLine}" +
        "Cada Tenant anunciado con «Retirado:» ya no existe; los demás siguen enteros. Corregida la causa, se " +
        "reanuda repitiendo la orden: los que ya no existen se saltan.";
}
