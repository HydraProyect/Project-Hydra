using CaeManager.Application.AltaAutonoma;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Coordinacion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.AltaAutonoma;

/// <summary>
/// Trabajo programado de la caducidad y el borrado de datos personales de las Solicitudes de alta (A8, DA-7): el
/// líder lanza <see cref="ServicioLimpiezaSolicitudesAlta"/> cada <see cref="LimpiezaSolicitudesAltaOptions.Intervalo"/>.
///
/// <para>
/// Mismo rol y mismo camino que las purgas existentes (<c>PurgaIniciosSesionHostedService</c>): la conexión de
/// <c>cae_app_runtime</c>, que no puede leer ni escribir <c>SolicitudesAlta</c>, y dos funciones <c>SECURITY
/// DEFINER</c> con los plazos dentro. La elección de líder evita que dos réplicas lo hagan a la vez; si coincidieran
/// (cambio de líder), <c>FOR UPDATE SKIP LOCKED</c> de las funciones reparte las filas sin doble trabajo.
/// </para>
/// </summary>
public class LimpiezaSolicitudesAltaHostedService(
    IServiceScopeFactory ambitoFactory,
    IEleccionLiderService eleccionLider,
    IOptions<LimpiezaSolicitudesAltaOptions> opciones,
    ILogger<LimpiezaSolicitudesAltaHostedService> logger)
    : BackgroundService
{
    internal static readonly TimeSpan IntervaloMinimo = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!opciones.Value.Activa)
        {
            logger.LogInformation("La limpieza de Solicitudes de alta está desactivada por configuración.");
            return;
        }

        // P41c: lo que hace este servicio lo hace la propia plataforma.
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        using var temporizador = new PeriodicTimer(IntervaloEfectivo(opciones.Value));
        await IntentarLimpiarAsync(stoppingToken);
        while (await temporizador.WaitForNextTickAsync(stoppingToken))
            await IntentarLimpiarAsync(stoppingToken);
    }

    internal static TimeSpan IntervaloEfectivo(LimpiezaSolicitudesAltaOptions opciones) =>
        opciones.Intervalo < IntervaloMinimo ? IntervaloMinimo : opciones.Intervalo;

    private async Task IntentarLimpiarAsync(CancellationToken stoppingToken)
    {
        try
        {
            await eleccionLider.IntentarEjecutarComoLiderAsync("limpieza-solicitudes-alta", LimpiarAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló la limpieza de Solicitudes de alta.");
        }
    }

    private async Task LimpiarAsync(CancellationToken stoppingToken)
    {
        using var ambito = ambitoFactory.CreateScope();
        await ambito.ServiceProvider.GetRequiredService<ServicioLimpiezaSolicitudesAlta>().EjecutarAsync(stoppingToken);
    }
}
