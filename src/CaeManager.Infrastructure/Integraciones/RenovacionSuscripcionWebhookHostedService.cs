using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Coordinacion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// Renueva las <see cref="SuscripcionWebhook"/> próximas a expirar (Graph
/// solo admite ~2.94 días por suscripción de correo, ver
/// <c>Microsoft365GraphClient</c>) — un sondeo diario es de sobra frente a
/// esa ventana. Envuelto en elección de líder (P3-30), igual que
/// <see cref="IngestaWebhookHostedService"/>.
/// </summary>
public class RenovacionSuscripcionWebhookHostedService(
    IServiceScopeFactory ambitoFactory, IEleccionLiderService eleccionLider, ILogger<RenovacionSuscripcionWebhookHostedService> logger)
    : BackgroundService
{
    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromHours(24);

    /// <summary>
    /// El doble del sondeo (auditoría de colas, 2026-08-30): con ventana =
    /// intervalo de sondeo, un solo ciclo fallido (excepción, redeploy a
    /// mitad de ciclo) dejaba la siguiente ejecución, 24 h después, mirando
    /// una suscripción que para entonces ya había expirado de verdad —
    /// margen operativo prácticamente nulo ante cualquier fallo puntual.
    /// 48 h sigue cómodo bajo el máximo de Graph (~70.5 h): un ciclo se
    /// puede perder entero y el siguiente todavía la encuentra dentro de la
    /// ventana.
    /// </summary>
    private static readonly TimeSpan VentanaRenovacion = TimeSpan.FromHours(48);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var temporizador = new PeriodicTimer(IntervaloSondeo);

        await IntentarRenovarTodosLosTenantsAsync(stoppingToken);

        while (await temporizador.WaitForNextTickAsync(stoppingToken))
            await IntentarRenovarTodosLosTenantsAsync(stoppingToken);
    }

    private async Task IntentarRenovarTodosLosTenantsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await eleccionLider.IntentarEjecutarComoLiderAsync("renovacion-suscripcion-webhook-microsoft365", RenovarTodosLosTenantsAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló un ciclo de renovación de suscripciones de webhook.");
        }
    }

    private async Task RenovarTodosLosTenantsAsync(CancellationToken stoppingToken)
    {
        List<Guid> tenantsActivos;
        using (var ambito = ambitoFactory.CreateScope())
        {
            tenantsActivos = await ambito.ServiceProvider.GetRequiredService<ITenantsQueryContext>()
                .Tenants.Where(t => t.Estado == EstadoTenant.Activo)
                .Select(t => t.Id)
                .ToListAsync(stoppingToken);
        }

        foreach (var tenantId in tenantsActivos)
        {
            stoppingToken.ThrowIfCancellationRequested();

            // Aislado igual que ProcesadorAnalisisDocumentoHostedService: sin
            // este try/catch, un tenant con una conexión rota (token de
            // refresco inválido, Graph devolviendo un error persistente)
            // abortaba este foreach entero y dejaba sin renovar a los
            // siguientes — con una ventana de renovación de 24 h y un sondeo
            // diario, eso podía costarle a un tenant sano toda su ventana de
            // margen antes de Graph expirar la suscripción de verdad.
            try
            {
                await RenovarPendientesDelTenantAsync(tenantId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Falló la renovación de suscripciones de webhook del tenant {TenantId}; se continúa con el resto de tenants en este ciclo.",
                    tenantId);
            }
        }
    }

    private async Task RenovarPendientesDelTenantAsync(Guid tenantId, CancellationToken stoppingToken)
    {
        using var ambito = ambitoFactory.CreateScope();
        using var _ = AmbitoTenantExplicito.Establecer(tenantId);

        var suscripcionRepositorio = ambito.ServiceProvider.GetRequiredService<ISuscripcionWebhookRepository>();
        var proximasAExpirar = await suscripcionRepositorio.ObtenerProximasAExpirarAsync(VentanaRenovacion, stoppingToken);
        if (proximasAExpirar.Count == 0) return;

        var conexionRepositorio = ambito.ServiceProvider.GetRequiredService<IConexionIntegracionRepository>();
        var accesoGraph = ambito.ServiceProvider.GetRequiredService<AccesoGraphService>();
        var graphClient = ambito.ServiceProvider.GetRequiredService<IMicrosoft365GraphClient>();

        foreach (var suscripcion in proximasAExpirar)
        {
            var conexion = await conexionRepositorio.ObtenerPorIdAsync(suscripcion.ConexionIntegracionId, stoppingToken);

            // Deshabilitada por el usuario (Desconectar) o borrada: no hay
            // nada que renovar ni ninguna razón para marcarla con error —
            // antes de este fichero cablear salud de plataforma (A-07),
            // este bucle ignoraba el estado de la conexión y lo intentaba
            // igual, contradiciendo el comentario de DesconectarBuzonCommand
            // que ya prometía este comportamiento.
            if (conexion is null || conexion.Estado == EstadoConexionIntegracion.Deshabilitada)
                continue;

            var accessTokenResultado = await accesoGraph.ObtenerAccessTokenVigenteAsync(suscripcion.ConexionIntegracionId, stoppingToken);
            if (accessTokenResultado.EsFallido)
            {
                logger.LogWarning(
                    "No se pudo renovar la suscripción {SubscriptionId} (conexión {ConexionId}): {Error}",
                    suscripcion.GraphSubscriptionId, suscripcion.ConexionIntegracionId, accessTokenResultado.Error.Mensaje);
                conexion.MarcarConError(accessTokenResultado.Error.Mensaje);
                continue;
            }

            var renovacionResultado = await graphClient.RenovarSuscripcionAsync(
                accessTokenResultado.Valor, suscripcion.GraphSubscriptionId, stoppingToken);
            if (renovacionResultado.EsFallido)
            {
                logger.LogWarning(
                    "No se pudo renovar la suscripción {SubscriptionId} (conexión {ConexionId}): {Error}",
                    suscripcion.GraphSubscriptionId, suscripcion.ConexionIntegracionId, renovacionResultado.Error.Mensaje);
                conexion.MarcarConError(renovacionResultado.Error.Mensaje);
                continue;
            }

            suscripcion.ActualizarTrasRenovacion(renovacionResultado.Valor.GraphSubscriptionId, renovacionResultado.Valor.FechaExpiracionUtc);

            // Recuperación automática: si la conexión había quedado ConError
            // por un ciclo anterior y esta renovación funcionó, no hace
            // falta que un administrador pulse "Reactivar" a mano.
            if (conexion.Estado == EstadoConexionIntegracion.ConError)
                conexion.Rehabilitar();
        }

        await ambito.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(stoppingToken);
    }
}
