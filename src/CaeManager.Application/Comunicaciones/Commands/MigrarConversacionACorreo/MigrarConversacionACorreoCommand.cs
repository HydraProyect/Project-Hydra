using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Commands.MigrarConversacionACorreo;

/// <summary>
/// Fallback de canal (docs/COMUNICACIONES.md § 16.5): la ventana de servicio
/// de WhatsApp (24 h desde el último entrante) ya cerró, así que el envío
/// libre por ese canal está bloqueado — en vez del bloqueo seco, el gestor
/// puede continuar la MISMA conversación por correo. No hay ningún hilo de
/// Graph que preservar (la conversación nació en WhatsApp), así que se envía
/// un mensaje nuevo, no una respuesta — mismo patrón que
/// <c>EnviarMensajeNuevoCommand</c>, pero añadiendo el mensaje a la
/// <see cref="Conversacion"/> ya existente en vez de crear una nueva.
///
/// Deliberadamente no toca <see cref="Conversacion.ConexionIntegracionId"/>:
/// sigue apuntando a la línea de WhatsApp, así que la ingesta de webhook
/// sigue sumando entrantes de WhatsApp a este mismo hilo con normalidad.
/// Limitación conocida y aceptada: una respuesta del cliente a ESTE correo
/// no threadea de vuelta a la conversación todavía — eso exige el
/// Conversation Matching Engine (§ 13.2, paso 4, sin construir).
/// </summary>
public record MigrarConversacionACorreoCommand(Guid ConversacionId, string EmailDestino, string CuerpoHtml) : ICommand;

public class MigrarConversacionACorreoCommandValidator : AbstractValidator<MigrarConversacionACorreoCommand>
{
    public MigrarConversacionACorreoCommandValidator()
    {
        RuleFor(c => c.ConversacionId).NotEmpty().WithMessage("La migración debe indicar una conversación.");
        RuleFor(c => c.EmailDestino).NotEmpty().EmailAddress().WithMessage("Indica un correo de destino válido.");
        RuleFor(c => c.CuerpoHtml).NotEmpty().WithMessage("El mensaje no puede estar vacío.");
    }
}

public class MigrarConversacionACorreoCommandHandler(
    IConversacionRepository conversacionRepositorio,
    IIntegracionesQueryContext integracionesContext,
    IConexionIntegracionRepository conexionRepositorio,
    IAlcanceDatosService alcanceDatos,
    IMicrosoft365GraphClient graphClient,
    AccesoGraphService accesoGraph,
    IResolucionParticipanteConversacionService resolucionParticipante,
    IUnitOfWork unitOfWork)
    : IRequestHandler<MigrarConversacionACorreoCommand, Result>
{
    public async Task<Result> Handle(MigrarConversacionACorreoCommand request, CancellationToken cancellationToken)
    {
        var conversacion = await conversacionRepositorio.ObtenerPorIdAsync(request.ConversacionId, cancellationToken);
        if (conversacion is null || !await alcanceDatos.ConversacionVisibleAsync(conversacion.ClienteId, conversacion.EmpresaId, conversacion.ConexionIntegracionId, cancellationToken))
            return Result.Fallo(Error.Crear("Conversacion.NoEncontrada", "No encontramos esta conversación."));

        if (conversacion.Canal != CanalConversacion.WhatsApp)
            return Result.Fallo(Error.Crear(
                "Conversacion.CanalIncorrecto", "Solo una conversación de WhatsApp puede migrar a correo."));

        if (conversacion.VentanaServicioAbierta(DateTime.UtcNow))
            return Result.Fallo(Error.Crear(
                "Conversacion.VentanaAbierta", "La ventana de WhatsApp sigue abierta — responde por ese canal."));

        // GestorPropietarioId != null excluido a propósito — ver el mismo
        // comentario en EnviarReclamacionCommand: un buzón personal también
        // tiene ClienteId null, y la migración a correo debe salir por un
        // buzón compartido del tenant, nunca por el personal de un gestor.
        var conexionId = await integracionesContext.ConexionesIntegracion
            .Where(c => c.Proveedor == ProveedorIntegracion.Microsoft365 && c.Estado == EstadoConexionIntegracion.Habilitada)
            .Where(c => c.GestorPropietarioId == null)
            .Where(c => c.ClienteId == conversacion.ClienteId || c.ClienteId == null)
            // Buzón dedicado al Cliente de la conversación antes que el buzón general del tenant.
            .OrderByDescending(c => c.ClienteId != null)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (conexionId is null)
            return Result.Fallo(Error.Crear(
                "Conversacion.SinBuzonConectado",
                "No hay ningún buzón de correo conectado — conéctalo en Integraciones antes de continuar por email."));

        var conexion = await conexionRepositorio.ObtenerPorIdAsync(conexionId.Value, cancellationToken);
        if (conexion is null)
            return Result.Fallo(Error.Crear(
                "Conversacion.SinBuzonConectado", "No hay ningún buzón de correo conectado."));

        var tokenResultado = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion.Id, cancellationToken);
        if (tokenResultado.EsFallido)
            return Result.Fallo(tokenResultado.Error);

        var envioResultado = await graphClient.EnviarNuevoMensajeAsync(
            tokenResultado.Valor, conexion.BuzonEmail, [request.EmailDestino], conversacion.Asunto, request.CuerpoHtml,
            adjuntos: null, cancellationToken);
        if (envioResultado.EsFallido)
            return Result.Fallo(envioResultado.Error);

        conversacion.AgregarMensaje(
            DireccionMensaje.Saliente, CanalConversacion.Correo, conexion.BuzonEmail, request.CuerpoHtml,
            mensajeExternoId: envioResultado.Valor.MensajeExternoId);
        var (tipoOrigen, entidadRelacionadaId) = await resolucionParticipante.ResolverAsync(
            request.EmailDestino, conversacion.ClienteId, cancellationToken);
        conversacion.AgregarParticipante(request.EmailDestino, RolParticipante.Para, tipoOrigen, entidadRelacionadaId);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
