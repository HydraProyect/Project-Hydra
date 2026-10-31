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

    internal static bool ReaperturaVencida(DateTime? reabiertoEnUtc, DateOnly hoy) =>
        reabiertoEnUtc is { } r
        && DiaDeNegocio.De(DateTime.SpecifyKind(r, DateTimeKind.Utc)) < new DateOnly(hoy.Year, hoy.Month, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();
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
            var espera = correcto ? proximoCierre - reloj.GetUtcNow().UtcDateTime : TimeSpan.FromMinutes(15);
            await Task.Delay(espera > TimeSpan.Zero ? espera : TimeSpan.FromSeconds(1), reloj, stoppingToken);
        }
    }

    internal async Task ProcesarAsync(CancellationToken ct)
    {
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();
        List<(Guid Id, DateTime CreadoEnUtc)> tenants;
        using (var scope = scopes.CreateScope())
            tenants = (await scope.ServiceProvider.GetRequiredService<ITenantsQueryContext>().Tenants
                .Select(t => new { t.Id, t.CreadoEnUtc }).ToListAsync(ct))
                .Select(t => (t.Id, t.CreadoEnUtc)).ToList();
        List<Exception> errores = [];
        foreach (var (tenantId, creadoEnUtc) in tenants)
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
                // La recuperación arranca en el mes de alta del Tenant: antes no hay nada suyo que cerrar.
                var primero = DiaDeNegocio.De(DateTime.SpecifyKind(creadoEnUtc, DateTimeKind.Utc));
                var desde = new DateOnly(primero.Year, primero.Month, 1);
                if (desde > mesAnterior) desde = mesAnterior;
                for (var mes = desde; mes <= mesAnterior; mes = mes.AddMonths(1))
                {
                    // Un mes reabierto se vuelve a cerrar cuando llega el primer día de un mes posterior al de la
                    // reapertura (día de negocio de Madrid): la reapertura dura hasta el próximo día 1 y la foto
                    // nueva sustituye a la anterior. Un arranque o reintento dentro del mismo mes no la toca.
                    var existente = await db.CierresMensualesTenantBeneficiario
                        .SingleOrDefaultAsync(c => c.Anyo == mes.Year && c.Mes == mes.Month, ct);
                    if (existente is not null && !ReaperturaVencida(existente.ReabiertoEnUtc, hoy))
                        continue;
                    var periodo = new MesCuadroDireccion(mes.Year, mes.Month);
                    var cifras = await medicion.MedirAsync(periodo, periodo.FinUtc, ct, medirDocumental: false);
                    var tarifas = await db.TarifasCliente.Select(t => new TarifaAplicadaCuadro(t.ClienteId, t.Concepto, t.PrecioUnitario, t.MonedaIso)).ToListAsync(ct);
                    var foto = new FotoCierreMensual(cifras, tarifas).Serializar();
                    if (existente is null)
                        db.CierresMensualesTenantBeneficiario.Add(CierreMensualTenantBeneficiario.Crear(
                            mes.Year, mes.Month, foto, periodo.CierreEsEstimado, ahora));
                    else
                        existente.Recerrar(foto, ahora);
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
