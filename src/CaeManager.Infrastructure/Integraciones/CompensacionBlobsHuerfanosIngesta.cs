using CaeManager.Application.Common;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// Compensación best-effort compartida por <see cref="IngestaWebhookHostedService"/>
/// e <see cref="IngestaWebhookWhatsAppHostedService"/> (auditoría módulo 6):
/// los adjuntos de un mensaje entrante se suben al almacenamiento DURANTE la
/// ingesta, pero el registro que los referencia (Mensaje/AdjuntoMensaje) se
/// guarda en una única transacción DESPUÉS, cuando el hosted service llama a
/// <c>SaveChangesAsync</c>. Si ese guardado falla, los blobs ya subidos se
/// quedan sin ninguna fila que los referencie — huérfanos para siempre, ya
/// que el evento se reintentará desde cero (mismo criterio que el borrador
/// huérfano de <c>EnviarNuevoMensajeAsync</c> y la suscripción huérfana de
/// <c>ConectarBuzonMicrosoft365Command</c>).
/// </summary>
internal static class CompensacionBlobsHuerfanosIngesta
{
    public static async Task EliminarSiOrfanosAsync(
        IReadOnlyList<string> archivosGuardados, IFileStorageService almacenamiento, ILogger logger)
    {
        foreach (var archivoUrl in archivosGuardados)
        {
            try
            {
                await almacenamiento.EliminarAsync(archivoUrl, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "No se pudo eliminar el blob huérfano {ArchivoUrl} tras un fallo de guardado en la ingesta de webhook.", archivoUrl);
            }
        }
    }
}
