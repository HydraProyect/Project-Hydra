using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;

/// <summary>
/// "Redactar" — a diferencia de <c>ResponderConversacionCommand</c>, no
/// contesta un hilo existente: abre uno nuevo (<c>/sendMail</c>, sin
/// <c>conversationId</c> previo que preservar) y crea la
/// <see cref="Conversacion"/> que lo representa en Hydra. Exige un
/// buzón conectado — a diferencia de responder, no tiene sentido "simular"
/// el primer envío de una conversación nueva (sí tiene sentido simular una
/// respuesta dentro de un hilo ya sembrado como dato de prueba).
/// </summary>
public record EnviarMensajeNuevoCommand(
    Guid ConexionIntegracionId, IReadOnlyList<string> Destinatarios, string Asunto, string CuerpoHtml,
    Guid? ClienteId = null, IReadOnlyList<AdjuntoParaEnviarDto>? Adjuntos = null) : ICommand<Guid>;

public class EnviarMensajeNuevoCommandValidator : AbstractValidator<EnviarMensajeNuevoCommand>
{
    public EnviarMensajeNuevoCommandValidator()
    {
        RuleFor(c => c.ConexionIntegracionId).NotEmpty().WithMessage("Selecciona el buzón desde el que enviar.");
        RuleFor(c => c.Destinatarios).NotEmpty().WithMessage("Indica al menos un destinatario.");
        RuleForEach(c => c.Destinatarios).EmailAddress().WithMessage("Alguno de los destinatarios no es un correo válido.");
        RuleFor(c => c.Asunto).NotEmpty().WithMessage("El asunto es obligatorio.").MaximumLength(Conversacion.LongitudMaximaAsunto);
        RuleFor(c => c.CuerpoHtml).NotEmpty().WithMessage("El mensaje no puede estar vacío.");
        RuleFor(c => c.Adjuntos)
            .Must(a => a is null || a.Sum(x => x.Contenido.LongLength) <= LimitesAdjuntosCorreo.TamanoMaximoTotalAdjuntosBytes)
            .WithMessage("Los adjuntos no pueden superar 3 MB en total.");
    }
}

public class EnviarMensajeNuevoCommandHandler(
    IConexionIntegracionRepository conexionRepositorio,
    IConversacionRepository conversacionRepositorio,
    IAlcanceDatosService alcanceDatos,
    IMicrosoft365GraphClient graphClient,
    AccesoGraphService accesoGraph,
    IFileStorageService almacenamiento,
    IUnitOfWork unitOfWork)
    : IRequestHandler<EnviarMensajeNuevoCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(EnviarMensajeNuevoCommand request, CancellationToken cancellationToken)
    {
        var conexion = await conexionRepositorio.ObtenerPorIdAsync(request.ConexionIntegracionId, cancellationToken);
        if (conexion is null || !await alcanceDatos.ClienteOpcionalVisibleAsync(conexion.ClienteId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("ConexionIntegracion.NoEncontrada", "No encontramos esta conexión."));

        if (conexion.Estado != EstadoConexionIntegracion.Habilitada)
            return Result.Fallo<Guid>(Error.Crear("ConexionIntegracion.NoDisponible", "Este buzón no está disponible."));

        // El Cliente del mensaje es el que pida quien redacta si se indica
        // (p. ej. desde una pantalla de Cliente concreta); si no, el que ya
        // tenga la propia conexión (buzón dedicado a un Cliente Delegante).
        var clienteId = request.ClienteId ?? conexion.ClienteId;
        if (clienteId is not null && !await alcanceDatos.ClienteVisibleAsync(clienteId.Value, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Cliente.NoEncontrado", "No encontramos este cliente."));

        var tokenResultado = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion.Id, cancellationToken);
        if (tokenResultado.EsFallido)
            return Result.Fallo<Guid>(tokenResultado.Error);

        var envioResultado = await graphClient.EnviarNuevoMensajeAsync(
            tokenResultado.Valor, conexion.BuzonEmail, request.Destinatarios, request.Asunto, request.CuerpoHtml, request.Adjuntos, cancellationToken);
        if (envioResultado.EsFallido)
            return Result.Fallo<Guid>(envioResultado.Error);

        var conversacion = new Conversacion(request.Asunto, clienteId);
        conversacionRepositorio.Agregar(conversacion);

        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Saliente, conversacion.Canal, conexion.BuzonEmail, request.CuerpoHtml);
        foreach (var destinatario in request.Destinatarios)
            conversacion.AgregarParticipante(destinatario, RolParticipante.Para, TipoParticipanteOrigen.Desconocido);

        if (request.Adjuntos is { Count: > 0 })
        {
            foreach (var adjunto in request.Adjuntos)
            {
                using var flujo = new MemoryStream(adjunto.Contenido);
                var archivoUrl = await almacenamiento.GuardarAsync(flujo, adjunto.NombreArchivo, cancellationToken);
                mensaje.AgregarAdjunto(adjunto.NombreArchivo, adjunto.TipoContenido, adjunto.Contenido.LongLength, archivoUrl);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito(conversacion.Id);
    }
}
