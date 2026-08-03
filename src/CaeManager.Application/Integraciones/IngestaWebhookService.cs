using CaeManager.Application.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// Consume la cola de <see cref="EventoWebhook"/> — invocado desde
/// <c>IngestaWebhookHostedService</c> (Infrastructure), nunca desde el
/// propio endpoint HTTP del webhook (ver ARQUITECTURA-INTEGRACIONES.md §
/// 6.4: nunca procesar Graph de forma síncrona dentro del request
/// entrante). No es un Command de MediatR a propósito: el hosted service ya
/// carga el <see cref="EventoWebhook"/> pendiente dentro de su propio scope
/// (mismo patrón que <c>ProcesadorAnalisisDocumentoHostedService</c>), así
/// que un Command que solo recibiera el Id obligaría a recargarlo dos veces
/// en la misma operación.
/// </summary>
public class IngestaWebhookService(
    IConexionIntegracionRepository conexionRepositorio,
    IConversacionCorreoRepository conversacionRepositorio,
    IMicrosoft365GraphClient graphClient,
    AccesoGraphService accesoGraph,
    IFileStorageService almacenamiento,
    ILogger<IngestaWebhookService> logger)
{
    // Tope defensivo, no un límite real de Graph: un adjunto de correo
    // legítimo rara vez supera esto, y descargar/guardar algo mucho más
    // grande en un job de fondo (sin límite de tiempo del request) es el
    // vector de abuso más barato de cerrar aquí sin construir nada nuevo.
    private const long TamanoMaximoAdjuntoEntranteBytes = 25 * 1024 * 1024;
    public async Task ProcesarAsync(EventoWebhook evento, CancellationToken cancellationToken)
    {
        try
        {
            await IngerirNotificacionAsync(evento, cancellationToken);
            evento.MarcarProcesado();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fallo procesando el evento de webhook {EventoId} (intento {Intento}).", evento.Id, evento.Intentos + 1);
            evento.RegistrarFallo(ex.Message);
        }
    }

    private async Task IngerirNotificacionAsync(EventoWebhook evento, CancellationToken cancellationToken)
    {
        var conexion = await conexionRepositorio.ObtenerPorIdAsync(evento.ConexionIntegracionId, cancellationToken);
        if (conexion is null || conexion.Estado != EstadoConexionIntegracion.Habilitada)
            return; // la conexión se borró/deshabilitó entre medias — se descarta sin reintentar, no es un fallo transitorio.

        var mensajeIds = graphClient.ExtraerMensajeIdsDeNotificacion(evento.PayloadCrudo);

        foreach (var mensajeId in mensajeIds)
            await IngerirMensajeAsync(conexion, mensajeId, cancellationToken);
    }

    private async Task IngerirMensajeAsync(ConexionIntegracion conexion, string mensajeId, CancellationToken cancellationToken)
    {
        var accessTokenResultado = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion.Id, cancellationToken);
        if (accessTokenResultado.EsFallido)
            throw new InvalidOperationException(accessTokenResultado.Error.Mensaje);

        var mensajeResultado = await graphClient.ObtenerMensajeAsync(accessTokenResultado.Valor, conexion.BuzonEmail, mensajeId, cancellationToken);
        if (mensajeResultado.EsFallido)
        {
            // Un mensaje puntual que Graph ya no tiene (borrado entre la
            // notificación y la ingesta) no debe tirar el resto del evento.
            logger.LogWarning(
                "No se pudo obtener el mensaje {MensajeId} de Graph para la conexión {ConexionId}: {Error}",
                mensajeId, conexion.Id, mensajeResultado.Error.Mensaje);
            return;
        }

        var mensaje = mensajeResultado.Valor;

        if (await conversacionRepositorio.ExisteMensajeExternoAsync(mensaje.MensajeExternoId, cancellationToken))
            return; // idempotencia: Graph puede reenviar la misma notificación más de una vez.

        var conversacion = await conversacionRepositorio.ObtenerPorHiloExternoAsync(mensaje.HiloExternoId, cancellationToken);
        if (conversacion is null)
        {
            conversacion = new ConversacionCorreo(mensaje.Asunto, conexion.ClienteId);
            conversacion.AsociarConexion(conexion.Id, mensaje.HiloExternoId);
            conversacionRepositorio.Agregar(conversacion);
        }

        var mensajeCreado = conversacion.AgregarMensaje(
            DireccionMensaje.Entrante, mensaje.RemitenteEmail, mensaje.CuerpoHtml, mensaje.FechaUtc, mensaje.MensajeExternoId);

        foreach (var participante in mensaje.Participantes)
            conversacion.AgregarParticipante(participante.Email, participante.Rol, TipoParticipanteOrigen.Desconocido);

        foreach (var adjunto in mensaje.Adjuntos)
            await DescargarYGuardarAdjuntoAsync(mensajeCreado, accessTokenResultado.Valor, mensaje.MensajeExternoId, adjunto, cancellationToken);
    }

    /// <summary>
    /// Mejor esfuerzo por adjunto — un adjunto que falla al descargar no debe
    /// perder el mensaje entero (ya persistido con su cuerpo, que es lo
    /// esencial). Se registra y se sigue con el resto.
    /// </summary>
    private async Task DescargarYGuardarAdjuntoAsync(
        MensajeCorreo mensaje, string accessToken, string mensajeExternoId, AdjuntoGraphDto adjunto, CancellationToken cancellationToken)
    {
        if (adjunto.TamanoBytes > TamanoMaximoAdjuntoEntranteBytes)
        {
            logger.LogWarning(
                "Adjunto {NombreArchivo} del mensaje {MensajeId} omitido: {TamanoBytes} bytes supera el máximo de {Maximo}.",
                adjunto.NombreArchivo, mensajeExternoId, adjunto.TamanoBytes, TamanoMaximoAdjuntoEntranteBytes);
            return;
        }

        var contenidoResultado = await graphClient.ObtenerContenidoAdjuntoAsync(accessToken, mensajeExternoId, adjunto.AdjuntoExternoId, cancellationToken);
        if (contenidoResultado.EsFallido)
        {
            logger.LogWarning(
                "No se pudo descargar el adjunto {AdjuntoId} ({NombreArchivo}) del mensaje {MensajeId}: {Error}",
                adjunto.AdjuntoExternoId, adjunto.NombreArchivo, mensajeExternoId, contenidoResultado.Error.Mensaje);
            return;
        }

        using var flujo = new MemoryStream(contenidoResultado.Valor);
        var archivoUrl = await almacenamiento.GuardarAsync(flujo, adjunto.NombreArchivo, cancellationToken);
        mensaje.AgregarAdjunto(adjunto.NombreArchivo, adjunto.TipoContenido, adjunto.TamanoBytes, archivoUrl);
    }
}
