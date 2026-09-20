using CaeManager.Application.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// Deja dicho en el arranque que el conector de Microsoft 365 está a medias.
/// Con <see cref="Microsoft365GraphOptions.EstaConfigurado"/> a <c>false</c> no se
/// registran la ingesta del webhook ni la renovación de la suscripción, y sin este
/// aviso la aplicación arranca sana, el panel dice «no configurado» y las
/// suscripciones de Graph ya creadas caducan a los ~3 días sin renovarse: el
/// correo de los buzones conectados deja de entrar y nada lo grita. Solo se
/// registra cuando hay configuración a medias; no cambia cuándo se registran los
/// otros dos servicios y nunca imprime valores ni rutas.
/// </summary>
public class AvisoConfiguracionMicrosoft365HostedService(
    IOptions<Microsoft365GraphOptions> opciones,
    ILogger<AvisoConfiguracionMicrosoft365HostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // P41c: ver ServiciosDeFondoDeclaranActorDeSistemaTests. Este servicio no
        // escribe entidades, pero el trinquete no admite excepciones por «este no
        // escribe»: caducaría el día que escribiera.
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        var problemas = opciones.Value.ProblemasDeConfiguracion();
        if (problemas.Count > 0)
        {
            logger.LogWarning(
                "Conector de Microsoft 365: la configuración de {Seccion} está a medias ({Problemas}). " +
                "La ingesta de correo y la renovación de la suscripción NO se han registrado: las " +
                "suscripciones de Graph ya creadas caducarán en unos 3 días sin renovarse y el correo " +
                "de los buzones conectados dejará de entrar.",
                Microsoft365GraphOptions.SeccionConfiguracion, string.Join("; ", problemas));
        }

        await Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
