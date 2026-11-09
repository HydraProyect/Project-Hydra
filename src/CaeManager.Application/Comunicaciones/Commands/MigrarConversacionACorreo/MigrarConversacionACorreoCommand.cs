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
/// Fallback de canal (Project-Hydra-Negocio/tecnico/docs/COMUNICACIONES.md § 16.5): la ventana de servicio
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
        RuleFor(c => c.CuerpoHtml)
            .Must(texto => texto is null || VariablesPlantillaRespuesta.Pendientes(texto).Count == 0)
            .WithMessage("Completa las variables de la plantilla antes de enviar.");
        RuleFor(c => c.ConversacionId).NotEmpty().WithMessage("La migración debe indicar una conversación.");
        RuleFor(c => c.EmailDestino).NotEmpty().EmailAddress().WithMessage("Indica un correo de destino válido.");
        RuleFor(c => c.CuerpoHtml).NotEmpty().WithMessage("El mensaje no puede estar vacío.");
    }
}

public class MigrarConversacionACorreoCommandHandler(
    IConversacionRepository conversacionRepositorio,
    IComunicacionesQueryContext comunicaciones,
    IIntegracionesQueryContext integracionesContext,
    IConexionIntegracionRepository conexionRepositorio,
    IAlcanceDatosService alcanceDatos,
    IMicrosoft365GraphClient graphClient,
    AccesoGraphService accesoGraph,
    IResolucionParticipanteConversacionService resolucionParticipante,
    IUnitOfWork unitOfWork,
    IRegistroExportacionService registroSalida)
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

        var bloqueo = await comunicaciones.ComprobarEnvioAsync(conversacion.Id, request.CuerpoHtml, cancellationToken);
        if (bloqueo.EsFallido)
            return bloqueo;

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
            // Dentro de cada grupo, una verificada antes que una sin verificar: un buzón general antiguo que ha perdido la verificación no tapa a otro verificado.
            .ThenByDescending(CaeManager.Application.Integraciones.ConsultasConexionIntegracion.VerificadaVigente(DateTime.UtcNow))
            // Varios buzones generales (los compartidos del Tenant): el conectado primero, para que la elección no dependa del orden de la base de datos.
            .ThenBy(c => c.FechaConectadaUtc)
            .ThenBy(c => c.Id)
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

        // Sin la identidad del buzón verificada no se sabe qué buzón responde hoy a su dirección: no sale nada.
        if (!conexion.OperativaVerificada(DateTime.UtcNow))
            return Result.Fallo(ErroresBuzonSinVerificar.Crear());

        await registroSalida.RegistrarSalidaAsync(
            OrigenSalidaComunicacion.MigracionACorreo, conexion.Id, [request.EmailDestino], copia: null, adjuntos: null, cancellationToken);

        var tokenResultado = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion, cancellationToken);
        if (tokenResultado.EsFallido)
            return Result.Fallo(tokenResultado.Error);

        // Puerta justo antes de enviar: se relee de la base, la instancia cargada antes puede estar obsoleta.
        if (!await conexionRepositorio.SigueOperativaVerificadaAsync(conexion.Id, DateTime.UtcNow, cancellationToken))
            return Result.Fallo(ErroresBuzonSinVerificar.Crear());

        // La misma puerta otra vez entre crear el borrador y enviarlo: son dos llamadas a Graph.
        var envioResultado = await graphClient.EnviarNuevoMensajeAsync(
            tokenResultado.Valor, BuzonGraph.De(conexion), [request.EmailDestino], conversacion.Asunto, request.CuerpoHtml,
            adjuntos: null, ct => conexionRepositorio.SigueOperativaVerificadaAsync(conexion.Id, DateTime.UtcNow, ct), cancellationToken);
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
