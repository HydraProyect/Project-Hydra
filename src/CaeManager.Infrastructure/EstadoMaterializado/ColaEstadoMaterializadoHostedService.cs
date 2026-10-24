using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Coordinacion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.EstadoMaterializado;

/// <summary>
/// Trabajo en segundo plano del estado materializado (diseño § 4.5, § 7.3 y § 13.8): consume la cola
/// <c>RecalculosPendientes</c> y, con <c>EstadoMaterializado:RellenoAutomatico</c> encendido (apagado por defecto; con el
/// cotejo nocturno en main se puede encender), rellena los Tenants que aún no tienen fila en <c>VersionesEstadoTenant</c>, con
/// <see cref="ProcesadorColaEstadoMaterializado"/>.
///
/// <para>
/// <b>Una sola instancia.</b> Envuelto en elección de líder (<see cref="IEleccionLiderService"/>, cerrojo de asesoramiento
/// de sesión): durante el relevo azul/verde las dos ranuras arrancan este servicio, pero solo la que tiene el cerrojo procesa
/// la cola en cada ciclo. Aunque las dos procesaran a la vez, el <c>FOR UPDATE SKIP LOCKED</c> de la cola y los cerrojos del
/// Tenant impiden que dos recálculos escriban lo mismo; la elección de líder solo evita el trabajo doble.
/// </para>
///
/// <para>
/// <b>Un Tenant propietario cada vez</b>, con el mismo mecanismo que <c>CierreEventosTerminadosHostedService</c>: recorre
/// todos los Tenants y abre un <see cref="AmbitoTenantExplicito"/> y un <c>CreateScope()</c> por cada uno. La conexión es
/// <c>cae_app_runtime</c> y la RLS de las tablas del estado solo deja ver y escribir las filas de ese Tenant.
/// </para>
///
/// <para>
/// Sondea cada <see cref="IntervaloSondeo"/>: hasta que exista el aviso de cola (PR 11 del plan), un cambio encolado espera
/// como mucho eso más lo que tarde el ciclo.
/// </para>
/// </summary>
public class ColaEstadoMaterializadoHostedService(
    IServiceScopeFactory ambitoFactory,
    IEleccionLiderService eleccionLider,
    ILogger<ColaEstadoMaterializadoHostedService> logger)
    : BackgroundService
{
    /// <summary>Clave de la elección de líder.</summary>
    public const string ClaveDeLider = "estado-materializado-cola";

    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // P41c: todo lo que hace este servicio de fondo lo hace la propia plataforma (ver también ProcesarAsync).
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
            await eleccionLider.IntentarEjecutarComoLiderAsync(ClaveDeLider, ProcesarAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló un ciclo de la cola del estado materializado.");
        }
    }

    /// <summary>
    /// Un ciclo sobre todos los Tenants, uno a uno. Interno para que los tests bajo <c>cae_app_runtime</c> ejerciten este
    /// mismo camino sin depender de la elección de líder ni del temporizador.
    /// </summary>
    internal async Task ProcesarAsync(CancellationToken stoppingToken)
    {
        // Se repite aquí, además de en ExecuteAsync, para que el test que llama a este método observe el mismo actor.
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

            // Aislado por Tenant: un fallo no deja sin procesar la cola del resto.
            try
            {
                using var _ = AmbitoTenantExplicito.Establecer(tenantId);
                using var ambito = ambitoFactory.CreateScope();
                await ambito.ServiceProvider.GetRequiredService<ProcesadorColaEstadoMaterializado>()
                    .ProcesarTenantAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Falló la cola del estado materializado del Tenant propietario {TenantId}; se continúa con el resto.", tenantId);
            }
        }
    }
}
