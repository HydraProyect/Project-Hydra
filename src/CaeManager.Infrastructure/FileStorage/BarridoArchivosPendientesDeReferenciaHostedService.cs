using CaeManager.Application.Common;
using CaeManager.Application.Common.Archivos;
using CaeManager.Application.Tenants;
using CaeManager.Infrastructure.Coordinacion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.FileStorage;

/// <summary>
/// Cada <see cref="IntervaloSondeo"/>, el líder recorre todos los Tenants (activos y suspendidos: un archivo huérfano de
/// un Tenant suspendido sigue ocupando su carpeta) y lanza en cada uno <see cref="BarridoArchivosPendientesDeReferencia"/>
/// (issue #2123). Mismo patrón de elección de líder, ámbito propio por Tenant propietario y ámbito de Tenant explícito
/// que <see cref="Comunicaciones.CierreConversacionesResueltasHostedService"/>: la conexión va como
/// <c>cae_app_runtime</c> bajo la RLS del Tenant de cada iteración, sin identidad nueva ni rol propietario. Siempre
/// registrado: no destruye nada que un Documento o una versión de plantilla referencie.
/// </summary>
public sealed class BarridoArchivosPendientesDeReferenciaHostedService(
    IServiceScopeFactory ambitoFactory,
    IEleccionLiderService eleccionLider,
    ILogger<BarridoArchivosPendientesDeReferenciaHostedService> logger)
    : BackgroundService
{
    /// <summary>Con filas que vencen a las 24 h, un archivo huérfano se borra entre las 24 y las 30 h.</summary>
    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // P41c: lo que hace este servicio lo hace la propia plataforma.
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        using var temporizador = new PeriodicTimer(IntervaloSondeo);
        await EjecutarCicloAsync(stoppingToken);
        while (await temporizador.WaitForNextTickAsync(stoppingToken))
            await EjecutarCicloAsync(stoppingToken);
    }

    private async Task EjecutarCicloAsync(CancellationToken stoppingToken)
    {
        try
        {
            await eleccionLider.IntentarEjecutarComoLiderAsync("barrido-archivos-pendientes-referencia", ProcesarTodosLosTenantsAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló un ciclo del barrido de archivos pendientes de referencia.");
        }
    }

    private async Task ProcesarTodosLosTenantsAsync(CancellationToken stoppingToken)
    {
        List<Guid> tenants;
        using (var ambito = ambitoFactory.CreateScope())
        {
            tenants = await ambito.ServiceProvider.GetRequiredService<ITenantsQueryContext>()
                .Tenants.Select(t => t.Id)
                .ToListAsync(stoppingToken);
        }

        foreach (var tenantId in tenants)
        {
            stoppingToken.ThrowIfCancellationRequested();

            // Un fallo en un Tenant no deja sin barrido al resto.
            try
            {
                using var ambito = ambitoFactory.CreateScope();
                using var _ = AmbitoTenantExplicito.Establecer(tenantId);
                var resultado = await ambito.ServiceProvider.GetRequiredService<BarridoArchivosPendientesDeReferencia>()
                    .BarrerAsync(stoppingToken);
                if (resultado.Borrados > 0 || resultado.Fallidos > 0)
                    logger.LogInformation(
                        "Barrido de archivos pendientes del Tenant {TenantId}: {Borrados} borrados, {Conservados} conservados, {Fallidos} fallidos.",
                        tenantId, resultado.Borrados, resultado.Conservados, resultado.Fallidos);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Falló el barrido de archivos pendientes del Tenant {TenantId}; se continúa con el resto de Tenants en este ciclo.",
                    tenantId);
            }
        }
    }
}
