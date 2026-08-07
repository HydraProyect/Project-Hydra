using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.ResponderConversacionWhatsApp;

/// <summary>Texto libre — WhatsApp no lleva HTML; el composer del chat envía texto plano.</summary>
public record ResponderConversacionWhatsAppCommand(Guid ConversacionId, string Texto) : ICommand;

public class ResponderConversacionWhatsAppCommandValidator : AbstractValidator<ResponderConversacionWhatsAppCommand>
{
    // Límite duro de la Cloud API para el body de un mensaje de texto.
    public const int LongitudMaximaTexto = 4096;

    public ResponderConversacionWhatsAppCommandValidator()
    {
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
/// </summary>
public class ResponderConversacionWhatsAppCommandHandler(
    IConversacionRepository conversacionRepositorio,
    IConexionIntegracionRepository conexionRepositorio,
    ILineaWhatsAppRepository lineaRepositorio,
    IAlcanceDatosService alcanceDatos,
    IWhatsAppCloudApiClient whatsAppClient,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ResponderConversacionWhatsAppCommand, Result>
{
    public async Task<Result> Handle(ResponderConversacionWhatsAppCommand request, CancellationToken cancellationToken)
    {
        var conversacion = await conversacionRepositorio.ObtenerPorIdAsync(request.ConversacionId, cancellationToken);
        // Mismo guard N-3 que el resto de handlers de Comunicaciones: sin
        // esto se podía responder en el hilo de otro gestor.
        if (conversacion is null || !await alcanceDatos.ClienteOpcionalVisibleAsync(conversacion.ClienteId, cancellationToken))
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

        var envio = await whatsAppClient.EnviarTextoAsync(
            linea.TokenAcceso, linea.PhoneNumberId, conversacion.TelefonoContacto, request.Texto, cancellationToken);
        if (envio.EsFallido)
            return Result.Fallo(envio.Error);

        var mensaje = conversacion.AgregarMensaje(
            DireccionMensaje.Saliente, linea.NumeroTelefono, request.Texto, mensajeExternoId: envio.Valor);
        mensaje.ActualizarEstadoEntrega(EstadoEntregaMensaje.Enviado);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
