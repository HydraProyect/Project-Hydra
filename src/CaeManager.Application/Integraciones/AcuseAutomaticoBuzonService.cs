using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Domain.Comunicaciones;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// Envía los acuses que dejó <see cref="IngestaWebhookService"/>. <c>IngestaWebhookHostedService</c> lo llama después de
/// confirmar la ingesta, en el mismo ámbito de Tenant, por dos razones: el reintento de un evento ve el mensaje ya
/// guardado y no repite el acuse, y el registro de salida se niega a guardar con cambios pendientes en el contexto.
/// La consecuencia es que un acuse sale como mucho una vez: si el proceso cae justo después de confirmar, no sale.
///
/// <para>
/// El tope por remitente se reserva y se confirma antes de enviar, y la reserva no se devuelve: Graph puede dar un fallo
/// después de aceptar el correo, y una reserva devuelta abriría otro acuse. Un envío fallido gasta el intervalo de ese
/// remitente. Se prefiere perder un acuse a contestar dos veces.
/// </para>
///
/// <para>
/// Nada de lo que falle aquí vuelve a la ingesta, ya confirmada. Los registros llevan solo Ids, el código del error
/// y el tipo de la excepción, nunca su mensaje: no es seguro que no repita una dirección.
/// </para>
/// </summary>
public class AcuseAutomaticoBuzonService(
    IConversacionRepository conversacionRepositorio,
    Domain.Integraciones.IConexionIntegracionRepository conexionRepositorio,
    IMicrosoft365GraphClient graphClient,
    AccesoGraphService accesoGraph,
    IRegistroExportacionService registroSalida,
    IUnitOfWork unitOfWork,
    IDescarteCambiosPendientes descarteCambios,
    ILogger<AcuseAutomaticoBuzonService> logger)
{
    /// <summary>
    /// Corte de bucles con un autorrespondedor que no se identifica y abre un hilo cada vez: la cabecera no lo delata y el
    /// hilo tampoco. Sin un tope por remitente, dos sistemas se contestarían sin fin.
    /// </summary>
    public static readonly TimeSpan IntervaloMinimoPorRemitente = TimeSpan.FromHours(24);

    public async Task EnviarAsync(IReadOnlyList<AcusePendiente> pendientes, CancellationToken cancellationToken)
    {
        foreach (var acuse in pendientes)
        {
            try
            {
                await EnviarUnoAsync(acuse, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // El correo ya está guardado y el evento cerrado: un apagado solo pierde este acuse.
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    "Falló el acuse de la conversación {ConversacionId} ({TipoExcepcion}): si ya había salido, queda sin registrar.",
                    acuse.Conversacion.Id, ex.GetType().Name);

                // Descartar suelta también las conversaciones del resto del lote: un acuse suyo saldría sin quedar registrado.
                descarteCambios.DescartarCambiosPendientes();
                return;
            }
        }
    }

    private async Task EnviarUnoAsync(AcusePendiente acuse, CancellationToken cancellationToken)
    {
        var (conexion, conversacion, mensajeExternoId, destinatario) = acuse;

        // La ingesta solo deja acuses de una conexión verificada, pero la verificación puede caducar entre medias.
        if (!conexion.OperativaVerificada(DateTime.UtcNow))
        {
            logger.LogWarning(
                "La conversación {ConversacionId} no lleva acuse: la identidad del buzón de su conexión no está verificada.",
                conversacion.Id);
            return;
        }

        var reservado = await conversacionRepositorio.ReservarAcuseAsync(
            conexion.TenantId, conexion.Id, destinatario, DateTime.UtcNow, IntervaloMinimoPorRemitente, cancellationToken);
        if (!reservado)
        {
            logger.LogInformation(
                "La conversación {ConversacionId} no lleva acuse: su remitente ya tiene uno de este buzón en el intervalo.",
                conversacion.Id);
            return;
        }

        // Antes de pedir el token, que puede dejar la credencial pendiente de guardar: ver RegistroSalidaComunicaciones.
        await registroSalida.RegistrarSalidaAsync(
            OrigenSalidaComunicacion.AcuseAutomatico, conexion.Id, [destinatario], copia: null, adjuntos: null, cancellationToken);

        var token = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion, cancellationToken);
        if (token.EsFallido)
        {
            logger.LogWarning(
                "No se envió el acuse de la conversación {ConversacionId}: sin token de acceso ({CodigoError}).",
                conversacion.Id, token.Error.Codigo);
            return;
        }

        // Antes de enviar: el token puede haberse renovado, Graph invalida el refresh token anterior, y una excepción del
        // envío descarta todo lo pendiente. Sin el token de parada, por lo mismo: la renovación ya ocurrió.
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        // Puerta justo antes de enviar: la conexión del acuse es la instancia que cargó la ingesta. Si entre medias
        // otro proceso la pasó a ConError o la desconectó (y quizá otro Tenant ya reclamó el buzón), no sale nada.
        if (!await conexionRepositorio.SigueOperativaVerificadaAsync(conexion.Id, DateTime.UtcNow, cancellationToken))
        {
            logger.LogWarning(
                "La conversación {ConversacionId} no lleva acuse: su conexión dejó de estar operativa antes del envío.",
                conversacion.Id);
            return;
        }

        // Por el cliente del contenedor: lo envuelve la guarda de envío C-04, que aquí decide igual que para una respuesta.
        var envio = await graphClient.EnviarRespuestaAsync(
            token.Valor, BuzonGraph.De(conexion), mensajeExternoId, PlantillaAcuseBuzon.CuerpoHtml, adjuntos: null, cancellationToken);

        if (envio.EsFallido)
        {
            logger.LogWarning(
                "No se envió el acuse de la conversación {ConversacionId} ({CodigoError}).", conversacion.Id, envio.Error.Codigo);
            return;
        }

        // Automático: no cuenta como respuesta de una persona ni para el plazo ni para la respuesta sugerida.
        conversacion.AgregarMensaje(
            DireccionMensaje.Saliente, conversacion.Canal, conexion.BuzonEmail, PlantillaAcuseBuzon.CuerpoHtml, esAutomatico: true);

        // Sin el token de parada porque el correo ya salió y este registro es el que lo muestra en la conversación.
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
    }
}
