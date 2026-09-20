using CaeManager.Application.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Configuracion;

/// <summary>
/// Un <c>LogWarning</c> por cada gate cerrado por configuración a medias: qué
/// sección, qué falta y qué deja de existir por ello. Reglas: nunca fatal (un
/// aviso no tumba el arranque) y sin valores — nombra opciones, jamás lo que
/// contienen.
/// </summary>
public class AvisoConfiguracionAMediasHostedService(
    IEnumerable<AvisoConfiguracionAMedias> avisos,
    ILogger<AvisoConfiguracionAMediasHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // P41c: ver ServiciosDeFondoDeclaranActorDeSistemaTests. Este servicio no
        // escribe entidades, pero el trinquete no admite excepciones por «este no
        // escribe»: caducaría el día que escribiera.
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        foreach (var aviso in avisos)
        {
            try
            {
                logger.LogWarning(
                    "Configuración a medias en {Seccion} ({Problemas}). {Consecuencia}",
                    aviso.Seccion, string.Join("; ", aviso.Problemas), aviso.Consecuencia);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "No se pudo avisar de la configuración a medias de {Seccion} ({Tipo}).",
                    aviso.Seccion, ex.GetType().Name);
            }
        }

        await Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
