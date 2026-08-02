using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace CaeManager.Infrastructure.Coordinacion;

/// <summary>
/// Comprueba al arrancar que el Redis de <c>SignalR:Redis</c> configurado
/// responde — mismo criterio que <c>VerificacionAlmacenamientoS3HostedService</c>/
/// <c>VerificacionDataProtectionS3HostedService</c>: una cadena de conexión
/// mal copiada no se nota hasta que dos réplicas reciben tráfico real y un
/// usuario pierde su circuito al cambiar de réplica a mitad de sesión.
/// </summary>
public class VerificacionSignalRRedisHostedService(
    IOptions<SignalRRedisOptions> opciones,
    ILogger<VerificacionSignalRRedisHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var config = opciones.Value;

        try
        {
            await using var conexion = await ConnectionMultiplexer.ConnectAsync(config.CadenaConexion!);
            await conexion.GetDatabase().PingAsync();

            logger.LogInformation(
                "Backplane de SignalR: Redis operativo. Los circuitos de Blazor Server " +
                "sobreviven a que el balanceador cambie de réplica.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Backplane de SignalR: SignalR:Redis está activado pero no se pudo conectar. " +
                "Revisa la cadena de conexión — mientras tanto, cada réplica sigue con su propio " +
                "backplane en memoria, así que un circuito puede perderse si el balanceador cambia de réplica.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
