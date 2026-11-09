using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// Renueva en Microsoft Graph las <see cref="SuscripcionWebhook"/> del Tenant actual próximas a expirar. Lo invoca
/// <c>RenovacionSuscripcionWebhookHostedService</c> una vez al día por Tenant propietario, con su ámbito explícito.
/// <list type="bullet">
/// <item>Cada conexión se guarda por separado y aislada: un fallo (de Graph, del guardado o una excepción) descarta
/// solo sus cambios pendientes y no se lleva por delante la renovación, la marca del hueco ni el ConError de las
/// demás conexiones del Tenant.</item>
/// <item>Si el guardado de una conexión colisiona por concurrencia (otro proceso cambió su versión mientras se hablaba
/// con Graph), lo observado no se pierde: se descarta lo pendiente, se recargan la conexión y su suscripción y se
/// vuelve a aplicar la misma observación, sin volver a llamar a Graph, hasta <see cref="IntentosGuardado"/> veces.
/// Las transiciones son idempotentes: la marca del hueco solo retrocede, el ConError no sobrescribe un motivo de
/// identidad y no mueve una Deshabilitada, y la renovación no rebaja una expiración ya posterior.</item>
/// <item>Marca del hueco: la regla conservadora (<see cref="ConexionIntegracion.RegistrarHuecoAlObservarSuscripcion"/>),
/// guardada con <see cref="CancellationToken.None"/> antes de pedir el token y renovar, y otra vez tras la respuesta con
/// la hora de después de la respuesta (<see cref="CompletarIdentidadBuzonService.MarcarConErrorDeRenovacion"/>). Es la
/// única fuente de la marca en este camino.</item>
/// </list>
/// Una conexión Deshabilitada (o borrada) no se renueva ni se marca, tampoco al reaplicar.
/// </summary>
public class RenovacionSuscripcionesWebhookService(
    ISuscripcionWebhookRepository suscripcionRepositorio,
    IConexionIntegracionRepository conexionRepositorio,
    AccesoGraphService accesoGraph,
    IMicrosoft365GraphClient graphClient,
    IUnitOfWork unitOfWork,
    IDescarteCambiosPendientes descarteCambios,
    ILogger<RenovacionSuscripcionesWebhookService> logger)
{
    /// <summary>Guardados de una misma observación ante colisiones de concurrencia seguidas, contando el primero.</summary>
    public const int IntentosGuardado = 3;

    /// <summary>Lo que respondió Graph a la renovación de una suscripción, para volver a aplicarlo sin volver a llamar.</summary>
    private sealed record Observacion(
        string GraphSubscriptionId,
        DateTime ExpiracionAnteriorUtc,
        DateTime ObservadaUtc,
        Error? Fallo,
        SuscripcionGraphDto? Renovada);

    public async Task RenovarProximasAExpirarAsync(TimeSpan ventana, CancellationToken cancellationToken)
    {
        var conexiones = (await suscripcionRepositorio.ObtenerProximasAExpirarAsync(ventana, cancellationToken))
            .Select(s => s.ConexionIntegracionId)
            .Distinct()
            .ToList();

        foreach (var conexionId in conexiones)
            await RenovarAisladaAsync(conexionId, cancellationToken);
    }

    private async Task RenovarAisladaAsync(Guid conexionId, CancellationToken cancellationToken)
    {
        try
        {
            await RenovarAsync(conexionId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "No se pudo renovar la suscripción de la conexión {ConexionId}; se sigue con las demás.", conexionId);

            // Sin esto, el guardado de la conexión siguiente arrastraría (o volvería a fallar por) la mutación ajena.
            descarteCambios.DescartarCambiosPendientes();
        }
    }

    private async Task RenovarAsync(Guid conexionId, CancellationToken cancellationToken)
    {
        var conexion = await conexionRepositorio.ObtenerPorIdAsync(conexionId, cancellationToken);
        var suscripcion = await suscripcionRepositorio.ObtenerPorConexionAsync(conexionId, cancellationToken);

        // Deshabilitada por el usuario (Desconectar) o borrada: no hay nada que renovar ni ninguna razón para marcarla
        // con error, igual que promete DesconectarBuzonCommand.
        if (conexion is null || suscripcion is null || conexion.Estado == EstadoConexionIntegracion.Deshabilitada)
            return;

        // Regla conservadora del hueco, guardada antes de llamar a Graph (ConexionIntegracion.RegistrarHuecoAlObservarSuscripcion):
        // si la conexión no está Habilitada o su suscripción ya expiró, la marca retrocede a min(marca, expiración, ahora)
        // y llega a la base salga como salga la llamada. Solo se guarda si la marca cambia, así que una conexión sana
        // (Habilitada con la suscripción vigente) no hace este guardado y no gana ninguna colisión nueva. Si choca por
        // concurrencia, se recarga y se vuelve a aplicar como el guardado de la observación, hasta IntentosGuardado veces;
        // agotados, la excepción sale sin llamar a Graph y la conexión espera al siguiente ciclo.
        for (var intento = 1; ; intento++)
        {
            if (!conexion.RegistrarHuecoAlObservarSuscripcion(suscripcion.FechaExpiracionUtc, DateTime.UtcNow))
                break;

            try
            {
                await unitOfWork.SaveChangesAsync(CancellationToken.None);
                break;
            }
            catch (DbUpdateConcurrencyException) when (intento < IntentosGuardado)
            {
                logger.LogWarning(
                    "Colisión de concurrencia al guardar la marca de hueco de la conexión {ConexionId} antes de renovar (intento {Intento}): se recarga.",
                    conexionId, intento);

                descarteCambios.DescartarCambiosPendientes();
                var recargada = await conexionRepositorio.ObtenerPorIdAsync(conexionId, cancellationToken);
                var suscripcionRecargada = await suscripcionRepositorio.ObtenerPorConexionAsync(conexionId, cancellationToken);
                if (recargada is null || suscripcionRecargada is null || recargada.Estado == EstadoConexionIntegracion.Deshabilitada)
                    return;

                conexion = recargada;
                suscripcion = suscripcionRecargada;
            }
        }

        var observacion = await ObservarAsync(conexion, suscripcion, cancellationToken);
        Aplicar(conexion, suscripcion, observacion);

        for (var intento = 1; ; intento++)
        {
            try
            {
                // Sin la cancelación del llamador: lo que Graph ya respondió (también la marca) no se queda en memoria.
                await unitOfWork.SaveChangesAsync(CancellationToken.None);
                return;
            }
            catch (DbUpdateConcurrencyException) when (intento < IntentosGuardado)
            {
                logger.LogWarning(
                    "Colisión de concurrencia al guardar la renovación de la conexión {ConexionId} (intento {Intento}): se recarga y se vuelve a aplicar.",
                    conexionId, intento);

                descarteCambios.DescartarCambiosPendientes();
                var recargada = await conexionRepositorio.ObtenerPorIdAsync(conexionId, cancellationToken);
                var suscripcionRecargada = await suscripcionRepositorio.ObtenerPorConexionAsync(conexionId, cancellationToken);
                if (recargada is null || recargada.Estado == EstadoConexionIntegracion.Deshabilitada)
                    return;

                Aplicar(recargada, suscripcionRecargada, observacion);
            }
        }
    }

    /// <summary>
    /// La hora de la observación se toma DESPUÉS de la respuesta: una suscripción que expira mientras dura la petición y
    /// termina en un fallo transitorio cuenta como expirada, y la marca no queda posterior a la pérdida.
    /// </summary>
    private async Task<Observacion> ObservarAsync(
        ConexionIntegracion conexion, SuscripcionWebhook suscripcion, CancellationToken cancellationToken)
    {
        var token = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion, cancellationToken);
        if (token.EsFallido)
        {
            logger.LogWarning(
                "No se pudo renovar la suscripción {SubscriptionId} (conexión {ConexionId}): {Error}",
                suscripcion.GraphSubscriptionId, conexion.Id, token.Error.Mensaje);
            return new Observacion(suscripcion.GraphSubscriptionId, suscripcion.FechaExpiracionUtc, DateTime.UtcNow, token.Error, null);
        }

        var renovacion = await graphClient.RenovarSuscripcionAsync(token.Valor, suscripcion.GraphSubscriptionId, cancellationToken);
        var observadaUtc = DateTime.UtcNow;
        if (renovacion.EsFallido)
        {
            logger.LogWarning(
                "No se pudo renovar la suscripción {SubscriptionId} (conexión {ConexionId}): {Error}",
                suscripcion.GraphSubscriptionId, conexion.Id, renovacion.Error.Mensaje);
            return new Observacion(suscripcion.GraphSubscriptionId, suscripcion.FechaExpiracionUtc, observadaUtc, renovacion.Error, null);
        }

        return new Observacion(suscripcion.GraphSubscriptionId, suscripcion.FechaExpiracionUtc, observadaUtc, null, renovacion.Valor);
    }

    /// <summary>
    /// Aplica lo observado a la conexión y a su suscripción tal como están ahora, recién cargadas o no.
    /// <list type="bullet">
    /// <item>Solo si la suscripción sigue siendo la observada (mismo <see cref="SuscripcionWebhook.GraphSubscriptionId"/>).
    /// Si al recargar es otra (Reactivar o la verificación la sustituyeron entretanto), la observación habla de una
    /// suscripción que ya no es la de la conexión y se descarta sin mutar nada: ni ConError ni marca ni renovación.</item>
    /// <item>Fallo: la regla de <see cref="CompletarIdentidadBuzonService.MarcarConErrorDeRenovacion"/>, con la expiración
    /// y la hora de la observación, no las de la reaplicación.</item>
    /// <item>Renovada: solo si no tiene ya una expiración igual o posterior. Una conexión en ConError renueva igual su
    /// suscripción, pero no vuelve a Habilitada aquí: solo la rehabilita <see cref="CompletarIdentidadBuzonService"/>
    /// cuando verifica su identidad.</item>
    /// </list>
    /// </summary>
    private static void Aplicar(ConexionIntegracion conexion, SuscripcionWebhook? suscripcion, Observacion observacion)
    {
        if (suscripcion is null || suscripcion.GraphSubscriptionId != observacion.GraphSubscriptionId)
            return;

        if (observacion.Fallo is { } fallo)
        {
            CompletarIdentidadBuzonService.MarcarConErrorDeRenovacion(
                conexion, fallo, observacion.ExpiracionAnteriorUtc, observacion.ObservadaUtc);
            return;
        }

        if (observacion.Renovada is { } renovada
            && suscripcion.FechaExpiracionUtc < renovada.FechaExpiracionUtc)
        {
            suscripcion.ActualizarTrasRenovacion(renovada.GraphSubscriptionId, renovada.FechaExpiracionUtc);
        }
    }
}
