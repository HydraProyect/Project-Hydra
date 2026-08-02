using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.ResponderConversacion;

public record ResponderConversacionCommand(Guid ConversacionId, string CuerpoHtml) : ICommand;

public class ResponderConversacionCommandValidator : AbstractValidator<ResponderConversacionCommand>
{
    public ResponderConversacionCommandValidator()
    {
        RuleFor(c => c.ConversacionId).NotEmpty().WithMessage("La respuesta debe pertenecer a una conversación.");
        RuleFor(c => c.CuerpoHtml).NotEmpty().WithMessage("La respuesta no puede estar vacía.");
    }
}

/// <summary>
/// Si la conversación tiene un buzón real conectado (P3-33,
/// ConexionIntegracionId), envía la respuesta de verdad por Graph
/// (preservando threading vía /reply) antes de persistir el mensaje — un
/// envío fallido no debe dejar un mensaje "fantasma" que el cliente nunca
/// recibió. Si no hay conexión (datos sembrados, o un Cliente sin buzón
/// todavía conectado), cae al comportamiento original: solo persiste el
/// mensaje con un remitente simulado, sin regresión para las demos.
/// </summary>
public class ResponderConversacionCommandHandler(
    IConversacionCorreoRepository repositorio,
    IConexionIntegracionRepository conexionRepositorio,
    IAlcanceDatosService alcanceDatos,
    IMicrosoft365GraphClient graphClient,
    AccesoGraphService accesoGraph,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ResponderConversacionCommand, Result>
{
    private const string RemitenteSimuladoEmail = "equipo-cae@buzon-simulado.local";

    public async Task<Result> Handle(ResponderConversacionCommand request, CancellationToken cancellationToken)
    {
        var conversacion = await repositorio.ObtenerPorIdAsync(request.ConversacionId, cancellationToken);
        // Ver AsignarEjecutivoConversacionCommandHandler (hallazgo N-3): sin
        // esto se podía responder en el hilo de otro gestor.
        if (conversacion is null || !await alcanceDatos.ClienteOpcionalVisibleAsync(conversacion.ClienteId, cancellationToken))
            return Result.Fallo(Error.Crear("ConversacionCorreo.NoEncontrada", "No encontramos esta conversación."));

        if (conversacion.ConexionIntegracionId is { } conexionId)
        {
            var envioResultado = await EnviarPorGraphAsync(conversacion, conexionId, request.CuerpoHtml, cancellationToken);
            if (envioResultado.EsFallido)
                return envioResultado;
        }
        else
        {
            conversacion.AgregarMensaje(DireccionMensaje.Saliente, RemitenteSimuladoEmail, request.CuerpoHtml);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }

    private async Task<Result> EnviarPorGraphAsync(
        ConversacionCorreo conversacion, Guid conexionId, string cuerpoHtml, CancellationToken cancellationToken)
    {
        var conexion = await conexionRepositorio.ObtenerPorIdAsync(conexionId, cancellationToken);
        if (conexion is null || conexion.Estado != EstadoConexionIntegracion.Habilitada)
            return Result.Fallo(Error.Crear(
                "ConversacionCorreo.ConexionNoDisponible", "El buzón conectado a esta conversación no está disponible."));

        var ultimoMensajeEntrante = conversacion.Mensajes
            .Where(m => m.Direccion == DireccionMensaje.Entrante && m.MensajeExternoId is not null)
            .OrderByDescending(m => m.FechaUtc)
            .FirstOrDefault();
        if (ultimoMensajeEntrante is null)
            return Result.Fallo(Error.Crear(
                "ConversacionCorreo.SinMensajeOrigen", "No hay ningún mensaje entrante al que responder en este hilo."));

        var accessTokenResultado = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion.Id, cancellationToken);
        if (accessTokenResultado.EsFallido)
            return Result.Fallo(accessTokenResultado.Error);

        var envioResultado = await graphClient.EnviarRespuestaAsync(
            accessTokenResultado.Valor, conexion.BuzonEmail, ultimoMensajeEntrante.MensajeExternoId!, cuerpoHtml, cancellationToken);
        if (envioResultado.EsFallido)
            return envioResultado;

        // Sent Items no está en el recurso vigilado por la suscripción (solo
        // Inbox) — un mensaje Saliente propio nunca vuelve por webhook, así
        // que no necesita MensajeExternoId para idempotencia.
        conversacion.AgregarMensaje(DireccionMensaje.Saliente, conexion.BuzonEmail, cuerpoHtml);
        return Result.Exito();
    }
}
