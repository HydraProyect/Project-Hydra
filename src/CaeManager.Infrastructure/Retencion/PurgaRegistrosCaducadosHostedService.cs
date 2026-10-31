using CaeManager.Application.Common;
using CaeManager.Infrastructure.Coordinacion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Retencion;

/// <summary>
/// Una vez al día, el líder lanza <see cref="PurgaRegistrosCaducados"/>. Siempre registrado, como
/// <see cref="RetencionHostedService"/>: el recuento debe correr aunque la purga esté apagada,
/// porque es lo que permite revisarla antes de encenderla.
/// </summary>
public class PurgaRegistrosCaducadosHostedService(
    IServiceScopeFactory ambitoFactory, IEleccionLiderService eleccionLider, ILogger<PurgaRegistrosCaducadosHostedService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // P41c: lo que hace este servicio lo hace la propia plataforma.
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        using var temporizador = new PeriodicTimer(Intervalo);
        await IntentarPurgarAsync(stoppingToken);
        while (await temporizador.WaitForNextTickAsync(stoppingToken))
            await IntentarPurgarAsync(stoppingToken);
    }

    private async Task IntentarPurgarAsync(CancellationToken stoppingToken)
    {
        try
        {
            await eleccionLider.IntentarEjecutarComoLiderAsync("purga-registros-auditoria", PurgarAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló la purga de registros de auditoría fuera de retención.");
        }
    }

    private async Task PurgarAsync(CancellationToken stoppingToken)
    {
        using var ambito = ambitoFactory.CreateScope();
        await ambito.ServiceProvider.GetRequiredService<PurgaRegistrosCaducados>().EjecutarAsync(stoppingToken);
    }
}
