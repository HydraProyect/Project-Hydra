using CaeManager.Application.Common;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Seguridad;

/// <summary>
/// Retención de 12 meses de <c>RegistrosInicioSesion</c>: una vez al día, el
/// líder llama a <c>app_purgar_inicios_sesion()</c>.
///
/// <para>
/// El rol de aplicación no tiene DELETE sobre la tabla (es append-only, como el
/// resto de la auditoría). La función la ejecuta su propietario, con el corte
/// de 12 meses escrito dentro y sin parámetros: el runtime puede pedir la purga
/// pero no adelantarla ni borrar una fila concreta.
/// </para>
///
/// <para>
/// En la misma pasada, la conservación de 12 meses de <c>SesionesUsuario</c> (sesiones de cookie
/// emitidas y revocadas) con <c>app_purgar_sesiones_usuario()</c>, con el mismo criterio: corte
/// fijo dentro de la función y sin DELETE para el runtime.
/// </para>
///
/// <para>
/// El log de abajo es solo operativo: la evidencia de cada borrado la deja la propia función en
/// <c>TrazasPurga</c>, en la misma transacción, también por llamada SQL directa.
/// </para>
/// </summary>
public class PurgaIniciosSesionHostedService(
    IServiceScopeFactory ambitoFactory, IEleccionLiderService eleccionLider, ILogger<PurgaIniciosSesionHostedService> logger)
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
            await eleccionLider.IntentarEjecutarComoLiderAsync("purga-inicios-sesion", PurgarAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló la purga de inicios de sesión fuera de retención.");
        }
    }

    private async Task PurgarAsync(CancellationToken stoppingToken)
    {
        using var ambito = ambitoFactory.CreateScope();
        var db = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var purgadas = await db.Database
            .SqlQuery<int>($"SELECT public.app_purgar_inicios_sesion() AS \"Value\"")
            .SingleAsync(stoppingToken);
        if (purgadas > 0)
            logger.LogInformation("Purgados {Filas} inicios de sesión con más de 12 meses.", purgadas);

        var sesiones = await db.Database
            .SqlQuery<int>($"SELECT public.app_purgar_sesiones_usuario() AS \"Value\"")
            .SingleAsync(stoppingToken);
        if (sesiones > 0)
            logger.LogInformation("Purgadas {Filas} sesiones de cookie con más de 12 meses.", sesiones);
    }
}
