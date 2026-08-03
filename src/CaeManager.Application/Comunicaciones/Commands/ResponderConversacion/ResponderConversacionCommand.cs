using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.ResponderConversacion;

public record ResponderConversacionCommand(
    Guid ConversacionId, string CuerpoHtml, IReadOnlyList<AdjuntoParaEnviarDto>? Adjuntos = null) : ICommand;

public class ResponderConversacionCommandValidator : AbstractValidator<ResponderConversacionCommand>
{
    public ResponderConversacionCommandValidator()
    {
        RuleFor(c => c.ConversacionId).NotEmpty().WithMessage("La respuesta debe pertenecer a una conversación.");
        RuleFor(c => c.CuerpoHtml).NotEmpty().WithMessage("La respuesta no puede estar vacía.");

        RuleFor(c => c.Adjuntos)
            .Must(a => a is null || a.Sum(x => x.Contenido.LongLength) <= LimitesAdjuntosCorreo.TamanoMaximoTotalAdjuntosBytes)
            .WithMessage("Los adjuntos no pueden superar 3 MB en total.");
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
    IFileStorageService almacenamiento,
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

        MensajeCorreo mensajeCreado;

        if (conversacion.ConexionIntegracionId is { } conexionId)
        {
            var envioResultado = await EnviarPorGraphAsync(conversacion, conexionId, request.CuerpoHtml, request.Adjuntos, cancellationToken);
            if (envioResultado.EsFallido)
                return Result.Fallo(envioResultado.Error);
            mensajeCreado = envioResultado.Valor;
        }
        else
        {
            mensajeCreado = conversacion.AgregarMensaje(DireccionMensaje.Saliente, RemitenteSimuladoEmail, request.CuerpoHtml);
        }

        // Los adjuntos se guardan en el propio storage independientemente de
        // si hubo envío real por Graph — el registro de "qué se adjuntó en
        // este hilo" no debe depender de que hubiera buzón conectado (mismo
        // criterio "sin regresión para las demos" del resto del handler).
        if (request.Adjuntos is { Count: > 0 })
            await GuardarAdjuntosAsync(mensajeCreado, request.Adjuntos, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }

    private async Task GuardarAdjuntosAsync(
        MensajeCorreo mensaje, IReadOnlyList<AdjuntoParaEnviarDto> adjuntos, CancellationToken cancellationToken)
    {
        foreach (var adjunto in adjuntos)
        {
            using var flujo = new MemoryStream(adjunto.Contenido);
            var archivoUrl = await almacenamiento.GuardarAsync(flujo, adjunto.NombreArchivo, cancellationToken);
            mensaje.AgregarAdjunto(adjunto.NombreArchivo, adjunto.TipoContenido, adjunto.Contenido.LongLength, archivoUrl);
        }
    }

    private async Task<Result<MensajeCorreo>> EnviarPorGraphAsync(
        ConversacionCorreo conversacion, Guid conexionId, string cuerpoHtml, IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos, CancellationToken cancellationToken)
    {
        var conexion = await conexionRepositorio.ObtenerPorIdAsync(conexionId, cancellationToken);
        if (conexion is null || conexion.Estado != EstadoConexionIntegracion.Habilitada)
            return Result.Fallo<MensajeCorreo>(Error.Crear(
                "ConversacionCorreo.ConexionNoDisponible", "El buzón conectado a esta conversación no está disponible."));

        var ultimoMensajeEntrante = conversacion.Mensajes
            .Where(m => m.Direccion == DireccionMensaje.Entrante && m.MensajeExternoId is not null)
            .OrderByDescending(m => m.FechaUtc)
            .FirstOrDefault();
        if (ultimoMensajeEntrante is null)
            return Result.Fallo<MensajeCorreo>(Error.Crear(
                "ConversacionCorreo.SinMensajeOrigen", "No hay ningún mensaje entrante al que responder en este hilo."));

        var accessTokenResultado = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion.Id, cancellationToken);
        if (accessTokenResultado.EsFallido)
            return Result.Fallo<MensajeCorreo>(accessTokenResultado.Error);

        var envioResultado = await graphClient.EnviarRespuestaAsync(
            accessTokenResultado.Valor, conexion.BuzonEmail, ultimoMensajeEntrante.MensajeExternoId!, cuerpoHtml, adjuntos, cancellationToken);
        if (envioResultado.EsFallido)
            return Result.Fallo<MensajeCorreo>(envioResultado.Error);

        // Sent Items no está en el recurso vigilado por la suscripción (solo
        // Inbox) — un mensaje Saliente propio nunca vuelve por webhook, así
        // que no necesita MensajeExternoId para idempotencia.
        return Result.Exito(conversacion.AgregarMensaje(DireccionMensaje.Saliente, conexion.BuzonEmail, cuerpoHtml));
    }
}
