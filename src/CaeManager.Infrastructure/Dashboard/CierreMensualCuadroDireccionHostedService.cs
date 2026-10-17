using CaeManager.Application.Common;
using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Dashboard;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Dashboard;

/// <summary>Cierre a las 00:00 de Madrid del día 1; recuperación idempotente tras reinicios, sin saltarse RLS.</summary>
public class CierreMensualCuadroDireccionHostedService(
    IServiceScopeFactory scopes, IEleccionLiderService lider, TimeProvider reloj,
    ILogger<CierreMensualCuadroDireccionHostedService> logger) : BackgroundService
{
    public static DateTime ProximoCierreUtc(DateTime ahoraUtc)
    {
        var hoy = DiaDeNegocio.De(ahoraUtc);
        return DiaDeNegocio.InicioEnUtc(new DateOnly(hoy.Year, hoy.Month, 1).AddMonths(1));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var proximoCierre = ProximoCierreUtc(reloj.GetUtcNow().UtcDateTime);
            var correcto = true;
            try
            {
                correcto = await lider.IntentarEjecutarComoLiderAsync("cuadro-direccion-cierre-mensual", ProcesarAsync, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                correcto = false;
                logger.LogError(ex, "Falló el cierre mensual del Cuadro de dirección; se reintentará.");
            }
            var espera = correcto ? proximoCierre - reloj.GetUtcNow().UtcDateTime : TimeSpan.FromMinutes(1);
            await Task.Delay(espera > TimeSpan.Zero ? espera : TimeSpan.FromSeconds(1), reloj, stoppingToken);
        }
    }

    internal async Task ProcesarAsync(CancellationToken ct)
    {
        using var actor = AmbitoActorAuditoria.EstablecerSistema();
        List<Guid> tenants;
        using (var scope = scopes.CreateScope())
            tenants = await scope.ServiceProvider.GetRequiredService<ITenantsQueryContext>().Tenants.Select(t => t.Id).ToListAsync(ct);
        List<Exception> errores = [];
        foreach (var tenantId in tenants)
        {
            try
            {
                using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
                var medicion = scope.ServiceProvider.GetRequiredService<MedicionMensualCuadroDireccion>();
                var ahora = reloj.GetUtcNow().UtcDateTime;
                var hoy = DiaDeNegocio.De(ahora);
                var mesAnterior = new DateOnly(hoy.Year, hoy.Month, 1).AddMonths(-1);
                var primeraFecha = await db.Trabajadores.IgnoreQueryFilters().Where(t => t.TenantId == tenantId)
                    .Select(t => (DateTime?)t.CreadoEnUtc)
                    .Concat(db.Centros.IgnoreQueryFilters().Where(c => c.TenantId == tenantId).Select(c => (DateTime?)c.CreadoEnUtc))
                    .Concat(db.Documentos.IgnoreQueryFilters().Where(d => d.TenantId == tenantId).Select(d => (DateTime?)d.CreadoEnUtc))
                    .Concat(db.Mensajes.IgnoreQueryFilters().Where(m => m.TenantId == tenantId).Select(m => (DateTime?)m.FechaUtc))
                    .Concat(db.Gestiones.IgnoreQueryFilters().Where(g => g.TenantId == tenantId).Select(g => g.CompletadaEnUtc))
                    .Concat(db.ReclamacionesDocumentales.IgnoreQueryFilters().Where(r => r.TenantId == tenantId).Select(r => (DateTime?)r.FechaEnvioUtc))
                    .MinAsync(ct);
                var primero = primeraFecha is null ? mesAnterior : DiaDeNegocio.De(primeraFecha.Value);
                var desde = new DateOnly(primero.Year, primero.Month, 1);
                if (desde > mesAnterior) desde = mesAnterior;
                for (var mes = desde; mes <= mesAnterior; mes = mes.AddMonths(1))
                {
                    if (await db.CierresMensualesTenantBeneficiario.AnyAsync(c => c.Anyo == mes.Year && c.Mes == mes.Month, ct))
                        continue;
                    var periodo = new MesCuadroDireccion(mes.Year, mes.Month);
                    var cifras = await medicion.MedirAsync(periodo, periodo.FinUtc, ct, medirDocumental: false);
                    var tarifas = await db.TarifasCliente.Select(t => new TarifaAplicadaCuadro(t.ClienteId, t.Concepto, t.PrecioUnitario, t.MonedaIso)).ToListAsync(ct);
                    db.CierresMensualesTenantBeneficiario.Add(CierreMensualTenantBeneficiario.Crear(
                        mes.Year, mes.Month, new FotoCierreMensual(cifras, tarifas).Serializar(), periodo.CierreEsEstimado, ahora));
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                errores.Add(ex);
                logger.LogError(ex, "Falló el cierre mensual del Tenant beneficiario {TenantId}.", tenantId);
            }
        }
        if (errores.Count > 0) throw new AggregateException(errores);
    }
}
