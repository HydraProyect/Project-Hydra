using CaeManager.Application.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Email;

/// <summary>
/// Deja dicho en el arranque en qué modo queda la guarda de envío, para que una producción sin
/// <c>EnvioCorreo__Modo</c> se vea en el log al desplegar y no cuando alguien pregunte por un correo.
/// Nunca fatal: lo que impide arrancar es la validación de <see cref="EnvioCorreoOptions"/>. No
/// escribe las direcciones de la lista, solo cuántas son.
/// </summary>
public sealed class AvisoGuardaEnvioCorreoHostedService(
    IOptions<EnvioCorreoOptions> opciones,
    ILogger<AvisoGuardaEnvioCorreoHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // P41c: ver ServiciosDeFondoDeclaranActorDeSistemaTests. Este servicio no escribe
        // entidades, pero el trinquete no admite excepciones por «este no escribe».
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        var config = opciones.Value;

        switch (config.ModoEfectivo)
        {
            case ModoEnvioCorreo.Cerrado:
                logger.LogWarning("Guarda de envío CERRADA: esta instancia no enviará ningún correo. Se abre con {Variable}.", "EnvioCorreo__Modo");
                break;
            case ModoEnvioCorreo.SoloPermitidos:
                logger.LogWarning("Guarda de envío en SoloPermitidos: solo salen correos a {Total} direcciones.", config.DestinatariosPermitidos.Length);
                break;
            default:
                logger.LogInformation("Guarda de envío abierta: esta instancia envía correo real.");
                break;
        }

        await Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
