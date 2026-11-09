using System.Security.Cryptography;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// Una conexión de Microsoft 365 que vuelve a Habilitada (desde ConError o desde Deshabilitada) tiene que tener antes
/// una suscripción de Graph vigente: sin ella quedaría operativa y verificada pero sin recibir notificaciones, y su
/// correo no entraría. Es la única puerta de esa transición: la usan la verificación de identidad del ciclo
/// (<see cref="CompletarIdentidadBuzonService"/>) y Reactivar.
/// <list type="bullet">
/// <item>Con <see cref="SuscripcionWebhook"/> guardada: se renueva en Graph. Renovar es la prueba de que existe; si
/// renueva, está vigente.</item>
/// <item>Sin ella, o con una renovación rechazada de forma definitiva (404 tras expirar, la que borró Desconectar): se
/// crea una nueva, con el mismo <see cref="SuscripcionWebhook.ClientState"/> si ya había una, y se guarda en la misma
/// fila.</item>
/// <item>Un fallo transitorio (<see cref="CodigosErrorMicrosoft365.EsTransitorio"/>) de la renovación o de la creación se
/// devuelve sin crear nada: el llamador no rehabilita y lo vuelve a intentar más tarde.</item>
/// <item>Un fallo definitivo de la creación se devuelve tal cual: el llamador deja la conexión en ConError (nunca una
/// Deshabilitada).</item>
/// </list>
/// No guarda: los cambios en la suscripción van en el mismo guardado que la rehabilitación.
/// </summary>
public class SuscripcionAlRehabilitarService(
    ISuscripcionWebhookRepository suscripcionRepositorio,
    IMicrosoft365GraphClient graphClient,
    ILogger<SuscripcionAlRehabilitarService> logger)
{
    /// <summary>La suscripción vigente: el Id de Graph y si se acaba de crear (para compensarla si el alta no sigue).</summary>
    public sealed record SuscripcionVigente(string GraphSubscriptionId, bool Creada);

    public async Task<Result<SuscripcionVigente>> AsegurarAsync(
        ConexionIntegracion conexion, string accessToken, CancellationToken cancellationToken)
    {
        var suscripcion = await suscripcionRepositorio.ObtenerPorConexionAsync(conexion.Id, cancellationToken);
        if (suscripcion is not null)
        {
            var renovacion = await graphClient.RenovarSuscripcionAsync(accessToken, suscripcion.GraphSubscriptionId, cancellationToken);
            if (renovacion.EsExitoso)
            {
                if (suscripcion.FechaExpiracionUtc < renovacion.Valor.FechaExpiracionUtc)
                    suscripcion.ActualizarTrasRenovacion(renovacion.Valor.GraphSubscriptionId, renovacion.Valor.FechaExpiracionUtc);
                return Result.Exito(new SuscripcionVigente(suscripcion.GraphSubscriptionId, Creada: false));
            }

            if (CodigosErrorMicrosoft365.EsTransitorio(renovacion.Error))
                return Result.Fallo<SuscripcionVigente>(renovacion.Error);

            logger.LogWarning(
                "La suscripción {SubscriptionId} de la conexión {ConexionId} ya no se puede renovar ({Error}): se crea otra antes de rehabilitarla.",
                suscripcion.GraphSubscriptionId, conexion.Id, renovacion.Error.Mensaje);
        }

        if (graphClient.UrlNotificacion(conexion.Id) is not { } urlNotificacion)
        {
            // Sin URL pública no hay a dónde mandar las notificaciones; no es cosa de la conexión, así que no la pasa a
            // ConError: se intenta de nuevo cuando la configuración exista.
            logger.LogError("Sin URL pública de Microsoft 365: la conexión {ConexionId} no se rehabilita sin suscripción.", conexion.Id);
            return Result.Fallo<SuscripcionVigente>(Error.Crear(
                CodigosErrorMicrosoft365.Transitorio, "No pudimos activar las notificaciones de este buzón ahora."));
        }

        var clientState = suscripcion?.ClientState ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var creada = await graphClient.CrearSuscripcionAsync(
            accessToken, BuzonGraph.De(conexion), urlNotificacion, clientState, cancellationToken);
        if (creada.EsFallido)
            return Result.Fallo<SuscripcionVigente>(creada.Error);

        if (suscripcion is null)
            suscripcionRepositorio.Agregar(new SuscripcionWebhook(
                conexion.Id, creada.Valor.GraphSubscriptionId, clientState, creada.Valor.FechaExpiracionUtc));
        else
            suscripcion.ActualizarTrasRenovacion(creada.Valor.GraphSubscriptionId, creada.Valor.FechaExpiracionUtc);

        return Result.Exito(new SuscripcionVigente(creada.Valor.GraphSubscriptionId, Creada: true));
    }

    /// <summary>
    /// Borra, sin bloquear, la suscripción que <see cref="AsegurarAsync"/> acaba de crear si la rehabilitación no llega a
    /// guardarse (p. ej. Reactivar pierde la reclamación del buzón): sin esto quedaría en Graph una suscripción huérfana.
    /// </summary>
    public async Task CompensarAsync(string accessToken, SuscripcionVigente vigente, CancellationToken cancellationToken)
    {
        if (!vigente.Creada)
            return;

        try
        {
            var eliminada = await graphClient.EliminarSuscripcionAsync(accessToken, vigente.GraphSubscriptionId, cancellationToken);
            if (eliminada.EsFallido)
                logger.LogWarning("No se pudo eliminar la suscripción huérfana {SubscriptionId}: {Error}", vigente.GraphSubscriptionId, eliminada.Error.Mensaje);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "No se pudo eliminar la suscripción huérfana {SubscriptionId}.", vigente.GraphSubscriptionId);
        }
    }
}
