using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.VincularConversacion;

/// <summary>
/// Confirma una propuesta del Conversation Matching Engine
/// (docs/COMUNICACIONES.md § 13.2): fusiona los mensajes de la conversación
/// WhatsApp <paramref name="ConversacionOrigenId"/> dentro de
/// <paramref name="ConversacionDestinoId"/> — nace un hilo mixto de verdad
/// (§ 13.1). Nunca lo dispara nada automático: el gestor confirma siempre,
/// desde la propuesta que le enseña ObtenerConversacionPorIdQuery.
///
/// Sin <c>Version</c>/ConcurrenciaOptimista a propósito: ningún otro Command
/// de Conversacion la usa hoy (CambiarEstadoConversacionCommand,
/// AsignarClienteConversacionCommand, AsignarEjecutivoConversacionCommand) —
/// añadirla solo aquí habría sido inconsistente con sus hermanos directos, y
/// generalizarla a los cuatro es un refactor propio, fuera de alcance de
/// esta pieza (CLAUDE.md — "no mezcles refactors independientes"). El riesgo
/// de doble clic es bajo: <see cref="Conversacion.AbsorberMensajesDe"/> mueve
/// la colección de Mensajes de origen, así que una segunda llamada no tiene
/// nada que mover — solo re-cierra una conversación ya cerrada y registra un
/// evento de vinculación duplicado en el timeline, nunca pérdida de datos.
/// </summary>
public record VincularConversacionCommand(Guid ConversacionOrigenId, Guid ConversacionDestinoId) : ICommand;

public class VincularConversacionCommandValidator : AbstractValidator<VincularConversacionCommand>
{
    public VincularConversacionCommandValidator()
    {
        RuleFor(c => c.ConversacionOrigenId).NotEmpty().WithMessage("Falta la conversación de origen.");
        RuleFor(c => c.ConversacionDestinoId).NotEmpty().WithMessage("Falta la conversación de destino.");
        RuleFor(c => c)
            .Must(c => c.ConversacionOrigenId != c.ConversacionDestinoId)
            .WithMessage("Una conversación no puede vincularse consigo misma.");
    }
}

public class VincularConversacionCommandHandler(
    IConversacionRepository repositorio, IEventoConversacionRepository eventoRepositorio,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<VincularConversacionCommand, Result>
{
    public async Task<Result> Handle(VincularConversacionCommand request, CancellationToken cancellationToken)
    {
        var origen = await repositorio.ObtenerPorIdAsync(request.ConversacionOrigenId, cancellationToken);
        if (origen is null || !await alcanceDatos.ConversacionVisibleAsync(origen.ClienteId, origen.ConexionIntegracionId, cancellationToken))
            return Result.Fallo(Error.Crear("Conversacion.NoEncontrada", "No encontramos la conversación de origen."));

        // Solo WhatsApp origina esta vinculación en la práctica (§ 13.2): un
        // hilo de correo ya llega asociado a su Cliente desde el enrutamiento
        // normal (§ 4.1) — no necesita "encontrar" otro hilo.
        if (origen.Canal != CanalConversacion.WhatsApp)
            return Result.Fallo(Error.Crear("Conversacion.CanalInvalido", "Solo una conversación de WhatsApp puede vincularse a otra."));

        if (origen.ClienteId is not { } clienteId)
            return Result.Fallo(Error.Crear("Conversacion.SinCliente", "La conversación de origen todavía no tiene un cliente asignado."));

        var destino = await repositorio.ObtenerPorIdAsync(request.ConversacionDestinoId, cancellationToken);
        // Mismo Cliente exigido en el servidor, no solo confiado del motor de
        // coincidencia: el candidato que propuso la UI pudo quedar desfasado
        // entre la lectura y la confirmación. Y alcance propio del destino
        // (auditoría módulo 6): compartir Cliente no basta si el destino
        // cuelga de un buzón personal de otro gestor.
        if (destino is null || destino.ClienteId != clienteId ||
            !await alcanceDatos.ConversacionVisibleAsync(destino.ClienteId, destino.ConexionIntegracionId, cancellationToken))
        {
            return Result.Fallo(Error.Crear("Conversacion.DestinoNoEncontrado", "No encontramos la conversación de destino."));
        }

        if (destino.Estado != EstadoConversacion.Abierta && destino.Estado != EstadoConversacion.Pendiente)
            return Result.Fallo(Error.Crear("Conversacion.DestinoNoDisponible", "La conversación de destino ya no está abierta."));

        destino.AbsorberMensajesDe(origen);
        origen.CambiarEstado(EstadoConversacion.Cerrada);

        eventoRepositorio.Agregar(new EventoConversacion(destino.Id, TipoEventoConversacion.ConversacionVinculada, origen.Id, DateTime.UtcNow));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
