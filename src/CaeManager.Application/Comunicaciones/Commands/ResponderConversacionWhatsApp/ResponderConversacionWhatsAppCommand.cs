using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Comunicaciones.Commands.ResponderConversacionWhatsApp;

/// <summary>Texto libre — WhatsApp no lleva HTML; el composer del chat envía texto plano.</summary>
[AreaDelEncargo("Comunicaciones")]
/// <param name="RespuestaSugeridaId">La sugerencia de Vega que insertó el redactor, si la hay; la valida el servidor (issue #2007).</param>
public record ResponderConversacionWhatsAppCommand(Guid ConversacionId, string Texto, Guid? RespuestaSugeridaId = null) : ICommand;

public class ResponderConversacionWhatsAppCommandValidator : AbstractValidator<ResponderConversacionWhatsAppCommand>
{
    // Límite duro de la Cloud API para el body de un mensaje de texto.
    public const int LongitudMaximaTexto = 4096;

    public ResponderConversacionWhatsAppCommandValidator()
    {
        RuleFor(c => c.Texto)
            .Must(texto => texto is null || VariablesPlantillaRespuesta.Pendientes(texto).Count == 0)
            .WithMessage("Completa las variables de la plantilla antes de enviar.");
        RuleFor(c => c.ConversacionId).NotEmpty().WithMessage("La respuesta debe pertenecer a una conversación.");
        RuleFor(c => c.Texto).NotEmpty().WithMessage("La respuesta no puede estar vacía.")
            .MaximumLength(LongitudMaximaTexto).WithMessage($"Un mensaje de WhatsApp no puede superar {LongitudMaximaTexto} caracteres.");
    }
}

/// <summary>
/// Command separado de <c>ResponderConversacionCommand</c> (correo) a
/// propósito: el pipeline es distinto — valida la ventana de servicio de
/// 24 h de Meta (fuera de ella solo se permiten plantillas aprobadas, no
/// soportadas en v1 → el envío libre se bloquea en servidor, no solo en la
/// UI), envía texto plano por la línea de la conversación y solo persiste
/// el mensaje si Meta lo aceptó (sin mensajes fantasma, mismo criterio que
/// el handler de correo). El wamid devuelto se guarda como MensajeExternoId
/// para casar los statuses (delivered/read/failed) posteriores.
///
/// Envío real y registro local no comparten transacción (auditoría módulo
/// 6): si Meta acepta el envío y el SaveChangesAsync posterior falla,
/// devolver un fallo invitaría a reintentar — y un reintento reenviaría el
/// texto de verdad, duplicado. Ese caso concreto se admite como éxito (con
/// el fallo de registro solo en el log) en vez de arriesgarlo.
/// </summary>
public class ResponderConversacionWhatsAppCommandHandler(
    IConversacionRepository conversacionRepositorio,
    IComunicacionesQueryContext comunicaciones,
    IConexionIntegracionRepository conexionRepositorio,
    ILineaWhatsAppRepository lineaRepositorio,
    IAlcanceDatosService alcanceDatos,
    IWhatsAppCloudApiClient whatsAppClient,
    IUnitOfWork unitOfWork,
    ILogger<ResponderConversacionWhatsAppCommandHandler> logger,
    IRegistroExportacionService registroSalida,
    IDescarteCambiosPendientes descarteCambios)
    : IRequestHandler<ResponderConversacionWhatsAppCommand, Result>
{
    public async Task<Result> Handle(ResponderConversacionWhatsAppCommand request, CancellationToken cancellationToken)
    {
        var conversacion = await conversacionRepositorio.ObtenerPorIdAsync(request.ConversacionId, cancellationToken);
        // Mismo guard N-3 que el resto de handlers de Comunicaciones: sin
        // esto se podía responder en el hilo de otro gestor.
        if (conversacion is null || !await alcanceDatos.ConversacionVisibleAsync(conversacion.ClienteId, conversacion.EmpresaId, conversacion.ConexionIntegracionId, cancellationToken))
            return Result.Fallo(Error.Crear("Conversacion.NoEncontrada", "No encontramos esta conversación."));

        if (conversacion.Canal != CanalConversacion.WhatsApp || conversacion.TelefonoContacto is null ||
            conversacion.ConexionIntegracionId is not { } conexionId)
        {
            return Result.Fallo(Error.Crear(
                "ConversacionWhatsApp.CanalIncorrecto", "Esta conversación no es de WhatsApp."));
        }

        if (!conversacion.VentanaServicioAbierta(DateTime.UtcNow))
            return Result.Fallo(Error.Crear(
                "ConversacionWhatsApp.FueraDeVentana",
                "Han pasado más de 24 horas desde el último mensaje del contacto; WhatsApp solo permite reabrir con una plantilla aprobada."));

        var conexion = await conexionRepositorio.ObtenerPorIdAsync(conexionId, cancellationToken);
        if (conexion is null || conexion.Estado != EstadoConexionIntegracion.Habilitada)
            return Result.Fallo(Error.Crear(
                "ConversacionWhatsApp.LineaNoDisponible", "La línea de WhatsApp de esta conversación no está disponible."));

        var linea = await lineaRepositorio.ObtenerPorConexionAsync(conexion.Id, cancellationToken);
        if (linea is null)
            return Result.Fallo(Error.Crear(
                "ConversacionWhatsApp.LineaNoDisponible", "La línea de WhatsApp de esta conversación no está disponible."));

        var bloqueo = await comunicaciones.ComprobarEnvioAsync(conversacion.Id, request.Texto, cancellationToken);
        if (bloqueo.EsFallido)
            return bloqueo;
        // WhatsApp no tiene cabeceras: el origen queda solo en el Mensaje (issue #2007).
        var origen = await comunicaciones.OrigenDelEnvioAsync(
            alcanceDatos, request.RespuestaSugeridaId, conversacion.Id, conexion.Id, request.Texto, cancellationToken);
        if (origen.EsFallido)
            return Result.Fallo(origen.Error);
        var sugerencia = origen.Valor;
        await registroSalida.RegistrarSalidaAsync(
            OrigenSalidaComunicacion.RespuestaWhatsApp, conexion.Id, [conversacion.TelefonoContacto], copia: null, adjuntos: null, cancellationToken);

        var envio = await whatsAppClient.EnviarTextoAsync(
            linea.TokenAcceso, linea.PhoneNumberId, conversacion.TelefonoContacto, request.Texto, cancellationToken);
        if (envio.EsFallido)
            return Result.Fallo(envio.Error);

        var mensaje = conversacion.AgregarMensaje(
            DireccionMensaje.Saliente, conversacion.Canal, linea.NumeroTelefono, request.Texto, mensajeExternoId: envio.Valor);
        mensaje.ActualizarEstadoEntrega(EstadoEntregaMensaje.Enviado);
        OrigenContenidoEnvio.RegistrarEn(mensaje, sugerencia);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "El mensaje se envió por WhatsApp (wamid {Wamid}) pero no se pudo registrar localmente en la conversación {ConversacionId}. Requiere reconciliación manual. Origen del contenido {OrigenContenido}, respuesta sugerida {RespuestaSugeridaId}.",
                envio.Valor, request.ConversacionId, mensaje.OrigenContenido, mensaje.RespuestaSugeridaId);
            descarteCambios.DescartarCambiosPendientes();
        }

        return Result.Exito();
    }
}
