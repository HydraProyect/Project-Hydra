using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Comunicaciones;

/// <summary>
/// Cierra las conversaciones que llevan <see cref="Conversacion.PlazoCierreAutomaticoTrasResolucion"/> (4 días) en estado
/// Resuelta (decisión de producto 2026-10-17, hueco 2 de Comunicaciones). La regla vive en
/// <see cref="Conversacion.CerrarSiResueltaHaceMasDelPlazo"/>; este servicio solo la aplica a las filas candidatas y la guarda.
/// Un mensaje entrante antes del plazo ya reabrió la conversación (<see cref="Conversacion.AgregarMensaje"/>), así que
/// llega aquí solo lo que de verdad nadie ha vuelto a tocar.
///
/// <para>
/// <b>Cierre de Sistema, sin autor.</b> El cambio queda en <see cref="Conversacion.CambiosEstado"/> con su instante y sin
/// persona, y en la auditoría general con <c>TipoActor = Sistema</c> (el ámbito se declara en el punto de entrada, como
/// en el resto de procesos programados). Nunca se atribuye al Gestor CAE que resolvió.
/// </para>
///
/// <para>
/// <b>Un Tenant propietario cada vez</b>, con el mismo mecanismo que <c>CierreEventosTerminadosHostedService</c> y
/// <c>ExpiracionAsignacionesHostedService</c>: recorre todos los Tenants (también los suspendidos) y abre un
/// <see cref="AmbitoTenantExplicito"/> y un <c>CreateScope()</c> por cada uno. La conexión es <c>cae_app_runtime</c> y la RLS
/// de <c>Conversaciones</c> solo deja ver y escribir las filas de ese Tenant; nunca una identidad que se salte la RLS.
/// </para>
///
/// Sin interruptor por Tenant: es una regla de la bandeja, no una automatización opcional (mismo criterio que el cierre de
/// eventos terminados en <c>CatalogoAutomatizaciones</c>). Envuelto en elección de líder: dos réplicas cerrando la misma
/// fila chocarían contra el token de concurrencia.
/// </summary>
public class CierreConversacionesResueltasHostedService(
    IServiceScopeFactory ambitoFactory,
    IEleccionLiderService eleccionLider,
    ILogger<CierreConversacionesResueltasHostedService> logger)
    : BackgroundService
{
    /// <summary>
    /// Una hora: el plazo se mide en días, así que una conversación se cierra como mucho una hora después de cumplirlo.
    /// </summary>
    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
            await eleccionLider.IntentarEjecutarComoLiderAsync(
                "cierre-conversaciones-resueltas", ProcesarAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló un ciclo de cierre de conversaciones resueltas.");
        }
    }

    /// <summary>
    /// Un ciclo sobre todos los Tenants, uno a uno. Interno para que un test bajo <c>cae_app_runtime</c> ejercite este mismo
    /// camino, interceptores incluidos, sin depender de la elección de líder ni del temporizador.
    /// </summary>
    internal async Task ProcesarAsync(CancellationToken stoppingToken) =>
        await ProcesarAsync(DateTime.UtcNow, stoppingToken);

    internal async Task ProcesarAsync(DateTime ahoraUtc, CancellationToken stoppingToken)
    {
        // Se repite en la entrada de cada ciclo, además de en ExecuteAsync, para que quien llame a este método sin pasar
        // por ExecuteAsync observe también el actor Sistema.
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

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

            // Aislado por Tenant: un fallo en uno no deja sin cerrar las del resto; lo que falló se reintenta en el pase
            // siguiente.
            try
            {
                await ProcesarTenantPropietarioAsync(tenantId, ahoraUtc, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Falló el cierre de conversaciones resueltas del Tenant propietario {TenantId}; se continúa con el resto.",
                    tenantId);
            }
        }
    }

    private async Task ProcesarTenantPropietarioAsync(Guid tenantId, DateTime ahoraUtc, CancellationToken stoppingToken)
    {
        using var _ = AmbitoTenantExplicito.Establecer(tenantId);
        using var ambito = ambitoFactory.CreateScope();
        var dbContext = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var limite = ahoraUtc - Conversacion.PlazoCierreAutomaticoTrasResolucion;
        var candidatas = await dbContext.Conversaciones
            .Where(c => c.Estado == EstadoConversacion.Resuelta && c.EstadoDesdeUtc <= limite)
            .ToListAsync(stoppingToken);

        var cerradas = candidatas.Count(c => c.CerrarSiResueltaHaceMasDelPlazo(ahoraUtc));
        if (cerradas == 0)
            return;

        await dbContext.SaveChangesAsync(stoppingToken);
        logger.LogInformation(
            "Cierre de conversaciones resueltas: {Cerradas} cerradas en el Tenant propietario {TenantId}.",
            cerradas, tenantId);
    }
}
