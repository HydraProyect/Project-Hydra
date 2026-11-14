using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// La Empresa propia de un Tenant propietario en las siembras de demostración (D-P11: una sola Empresa propia activa por
/// Tenant propietario). Es el único sitio de las siembras de demo que construye una Empresa propia, y la construye por el
/// mismo camino que el producto: el alta pasa por <see cref="ILimiteEmpresaPropia"/> (candado por Tenant y recuento
/// dentro del candado, guardado dentro del delegado), no por el constructor suelto de <see cref="Empresa"/>. Así la
/// prueba de arquitectura <c>AltaDeEmpresaPropiaSoloTrasElLimiteTests</c> no necesita exentar a ninguna siembra que use
/// este asistente, y una siembra que quisiera dejar una segunda Empresa propia recibiría el rechazo del límite en vez de
/// escribirla.
///
/// <para>
/// <b>Idempotente y sin abortar el arranque.</b> Si el Tenant ya tiene una Empresa propia activa (la de
/// <see cref="DatosPruebaSeeder"/> en los Tenants de la demo, o la de una siembra anterior de esta misma rama), se
/// reutiliza esa: la siembra se monta encima de lo que hay, no lo duplica ni lo rechaza. El recuento del límite solo
/// se ejercita cuando de verdad hay que crearla.
/// </para>
///
/// <para>
/// <b>Frontera.</b> No toca RLS ni autorización: el Tenant es el del ámbito explícito que ya tiene abierto la siembra
/// (<see cref="AmbitoTenantExplicito"/>), el mismo que sella la fila y el que cuenta el límite.
/// </para>
/// </summary>
internal static class SiembraEmpresaPropia
{
    /// <summary>
    /// La Empresa propia activa del Tenant <paramref name="tenantId"/>, creada con <paramref name="razonSocial"/> y
    /// <paramref name="cif"/> si no tenía ninguna. Se llama dentro del <see cref="AmbitoTenantExplicito"/> de ese Tenant.
    /// </summary>
    internal static async Task<Empresa> AsegurarAsync(
        CaeManagerDbContext dbContext, Guid tenantId, string razonSocial, string cif, CancellationToken cancellationToken)
    {
        var existente = await dbContext.Empresas
            .Where(e => e.TenantId == tenantId && e.EsPropia && !e.EstaEliminado)
            .OrderBy(e => e.RazonSocial)
            .FirstOrDefaultAsync(cancellationToken);
        if (existente is not null)
            return existente;

        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var limite = new LimiteEmpresaPropia(
            dbContext, tenantActual, new AlertaOperativaInerte(),
            new CandadoEmpresaPropia(dbContext, new TransaccionDeComando(dbContext), tenantActual));

        return await DarDeAltaAsync(dbContext, limite, razonSocial, cif, cancellationToken);
    }

    private static async Task<Empresa> DarDeAltaAsync(
        CaeManagerDbContext dbContext, ILimiteEmpresaPropia limite, string razonSocial, string cif, CancellationToken cancellationToken)
    {
        Empresa? propia = null;
        var resultado = await limite.SerializarAsync(async c =>
        {
            if (await limite.ComprobarAltaAsync(0, true, c) is { } error)
                return Result.Fallo(error);

            propia = new Empresa(razonSocial, cif);
            dbContext.Empresas.Add(propia);
            await dbContext.SaveChangesAsync(c);
            return Result.Exito();
        }, cancellationToken);

        return resultado.EsFallido || propia is null
            ? throw new InvalidOperationException(
                $"No se pudo sembrar la Empresa propia «{razonSocial}»: {resultado.Error.Mensaje}")
            : propia;
    }
}
