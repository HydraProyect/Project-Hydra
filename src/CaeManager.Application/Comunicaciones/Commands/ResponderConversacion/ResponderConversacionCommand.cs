using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
/// recibió. Si no hay conexión, falla con un error claro por defecto — ver
/// <see cref="ComunicacionesRemitenteOptions"/> — salvo que el entorno haya
/// activado a propósito el remitente simulado (solo pensado para datos
/// sembrados por <c>ComunicacionesDatosPruebaSeeder</c>, nunca producción).
///
/// Envío real y registro local son dos pasos que no comparten transacción
/// (auditoría módulo 6): si Graph acepta el envío y el SaveChangesAsync
/// posterior falla, el mensaje YA salió de verdad hacia el cliente. Devolver
/// un fallo aquí invitaría a reintentar — y un reintento volvería a
/// enviarlo, esta vez sí duplicado. Por eso ese caso concreto se admite como
/// éxito (con el fallo de registro solo en el log, para que se note y se
/// reconcilie) en vez de arriesgar un envío real duplicado.
/// </summary>
public class ResponderConversacionCommandHandler(
    IConversacionRepository repositorio,
    IConexionIntegracionRepository conexionRepositorio,
    IAlcanceDatosService alcanceDatos,
    IMicrosoft365GraphClient graphClient,
    AccesoGraphService accesoGraph,
    IFileStorageService almacenamiento,
    IOptions<ComunicacionesRemitenteOptions> opcionesRemitente,
    IUnitOfWork unitOfWork,
    ILogger<ResponderConversacionCommandHandler> logger)
    : IRequestHandler<ResponderConversacionCommand, Result>
{
    private const string RemitenteSimuladoEmail = "equipo-cae@buzon-simulado.local";

    public async Task<Result> Handle(ResponderConversacionCommand request, CancellationToken cancellationToken)
    {
        var conversacion = await repositorio.ObtenerPorIdAsync(request.ConversacionId, cancellationToken);
        // Ver AsignarEjecutivoConversacionCommandHandler (hallazgo N-3): sin
        // esto se podía responder en el hilo de otro gestor.
        if (conversacion is null || !await alcanceDatos.ConversacionVisibleAsync(conversacion.ClienteId, conversacion.EmpresaId, conversacion.ConexionIntegracionId, cancellationToken))
            return Result.Fallo(Error.Crear("Conversacion.NoEncontrada", "No encontramos esta conversación."));

        Mensaje mensajeCreado;
        var huboEnvioReal = false;

        if (conversacion.ConexionIntegracionId is { } conexionId)
        {
            var envioResultado = await EnviarPorGraphAsync(conversacion, conexionId, request.CuerpoHtml, request.Adjuntos, cancellationToken);
            if (envioResultado.EsFallido)
                return Result.Fallo(envioResultado.Error);
            mensajeCreado = envioResultado.Valor;
            huboEnvioReal = true;
        }
        else if (opcionesRemitente.Value.PermitirRemitenteSimulado)
        {
            mensajeCreado = conversacion.AgregarMensaje(DireccionMensaje.Saliente, conversacion.Canal, RemitenteSimuladoEmail, request.CuerpoHtml);
        }
        else
        {
            return Result.Fallo(Error.Crear(
                "Conversacion.SinBuzonConectado",
                "Esta conversación no tiene un buzón de correo conectado — conecta un buzón de Microsoft 365 antes de responder."));
        }

        // Los adjuntos se guardan en el propio storage independientemente de
        // si hubo envío real por Graph — el registro de "qué se adjuntó en
        // este hilo" no debe depender de que hubiera buzón conectado (mismo
        // criterio "sin regresión para las demos" del resto del handler).
        if (request.Adjuntos is { Count: > 0 })
            await GuardarAdjuntosAsync(mensajeCreado, request.Adjuntos, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (huboEnvioReal)
        {
            logger.LogError(ex,
                "El mensaje se envió por Graph pero no se pudo registrar localmente en la conversación {ConversacionId}. Requiere reconciliación manual.",
                request.ConversacionId);
            return Result.Exito();
        }

        return Result.Exito();
    }

    private async Task GuardarAdjuntosAsync(
        Mensaje mensaje, IReadOnlyList<AdjuntoParaEnviarDto> adjuntos, CancellationToken cancellationToken)
    {
        foreach (var adjunto in adjuntos)
        {
            using var flujo = new MemoryStream(adjunto.Contenido);
            var archivoUrl = await almacenamiento.GuardarAsync(flujo, adjunto.NombreArchivo, cancellationToken);
            mensaje.AgregarAdjunto(adjunto.NombreArchivo, adjunto.TipoContenido, adjunto.Contenido.LongLength, archivoUrl);
        }
    }

    private async Task<Result<Mensaje>> EnviarPorGraphAsync(
        Conversacion conversacion, Guid conexionId, string cuerpoHtml, IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos, CancellationToken cancellationToken)
    {
        var conexion = await conexionRepositorio.ObtenerPorIdAsync(conexionId, cancellationToken);
        if (conexion is null || conexion.Estado != EstadoConexionIntegracion.Habilitada)
            return Result.Fallo<Mensaje>(Error.Crear(
                "Conversacion.ConexionNoDisponible", "El buzón conectado a esta conversación no está disponible."));

        var ultimoMensajeEntrante = conversacion.Mensajes
            .Where(m => m.Direccion == DireccionMensaje.Entrante && m.MensajeExternoId is not null)
            .OrderByDescending(m => m.FechaUtc)
            .FirstOrDefault();
        if (ultimoMensajeEntrante is null)
            return Result.Fallo<Mensaje>(Error.Crear(
                "Conversacion.SinMensajeOrigen", "No hay ningún mensaje entrante al que responder en este hilo."));

        var accessTokenResultado = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion.Id, cancellationToken);
        if (accessTokenResultado.EsFallido)
            return Result.Fallo<Mensaje>(accessTokenResultado.Error);

        var envioResultado = await graphClient.EnviarRespuestaAsync(
            accessTokenResultado.Valor, conexion.BuzonEmail, ultimoMensajeEntrante.MensajeExternoId!, cuerpoHtml, adjuntos, cancellationToken);
        if (envioResultado.EsFallido)
            return Result.Fallo<Mensaje>(envioResultado.Error);

        // Sent Items no está en el recurso vigilado por la suscripción (solo
        // Inbox) — un mensaje Saliente propio nunca vuelve por webhook, así
        // que no necesita MensajeExternoId para idempotencia.
        return Result.Exito(conversacion.AgregarMensaje(DireccionMensaje.Saliente, conversacion.Canal, conexion.BuzonEmail, cuerpoHtml));
    }
}
