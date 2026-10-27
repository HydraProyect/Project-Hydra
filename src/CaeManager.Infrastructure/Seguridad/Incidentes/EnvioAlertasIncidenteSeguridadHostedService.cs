using CaeManager.Application.Common;
using Microsoft.Extensions.Hosting;

namespace CaeManager.Infrastructure.Seguridad.Incidentes;

/// <summary>
/// Único lector de <see cref="ColaAlertasIncidenteSeguridad"/>: saca cada incidente encolado y lo
/// envía por el canal con los reintentos de la cola. Va separado de la cola para que esta sea un
/// singleton corriente (la inyectan las señales) y el servicio de fondo se registre con
/// <c>AddHostedService&lt;T&gt;()</c> y declare su actor de sistema.
/// </summary>
public sealed class EnvioAlertasIncidenteSeguridadHostedService(ColaAlertasIncidenteSeguridad cola) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();
        try
        {
            await foreach (var incidenteId in cola.Lector.ReadAllAsync(stoppingToken))
                await cola.EnviarConReintentosAsync(incidenteId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Parada ordenada.
        }
    }
}
