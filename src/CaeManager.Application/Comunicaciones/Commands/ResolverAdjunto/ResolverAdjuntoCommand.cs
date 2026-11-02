using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.ResolverAdjunto;

public enum ResolucionAdjunto
{
    Rechazar = 0,
    Descartar = 1,
    Reabrir = 2
}

public record ResolverAdjuntoCommand(Guid AdjuntoId, ResolucionAdjunto Resolucion, string? Motivo = null) : ICommand<Guid>;

public class ResolverAdjuntoCommandValidator : AbstractValidator<ResolverAdjuntoCommand>
{
    public ResolverAdjuntoCommandValidator()
    {
        RuleFor(c => c.AdjuntoId).NotEmpty();
        RuleFor(c => c.Resolucion).IsInEnum();
        RuleFor(c => c.Motivo)
            .NotEmpty().When(c => c.Resolucion == ResolucionAdjunto.Rechazar)
            .WithMessage("Indica por qué se rechaza el adjunto.");
        RuleFor(c => c.Motivo)
            .Must(m => m is null || m.Trim().Length <= AdjuntoMensaje.LongitudMaximaMotivoRechazo)
            .WithMessage($"El motivo no puede superar {AdjuntoMensaje.LongitudMaximaMotivoRechazo} caracteres.");
    }
}

/// <summary>
/// Rechaza o descarta un adjunto entrante, o deshace esa decisión. Queda dentro del Tenant: <b>no avisa al remitente
/// ni envía nada</b>. El rol lo decide <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>; aquí se añade
/// el alcance sobre el hilo.
/// </summary>
public class ResolverAdjuntoCommandHandler(
    IComunicacionesQueryContext comunicacionesContext, IAdjuntoMensajeRepository repositorio,
    IEventoConversacionRepository eventoRepositorio, IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService, IUnitOfWork unitOfWork)
    : IRequestHandler<ResolverAdjuntoCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ResolverAdjuntoCommand request, CancellationToken cancellationToken)
    {
        var origen = await RepartoAdjuntos.ObtenerAsync(comunicacionesContext, request.AdjuntoId, cancellationToken);
        if (origen is null || !await origen.VisibleAsync(alcanceDatos, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Adjunto.NoEncontrado", "No encontramos este adjunto."));

        if (origen.Direccion != DireccionMensaje.Entrante)
            return Result.Fallo<Guid>(Error.Crear("Adjunto.NoEntrante", "Solo se reparten los adjuntos recibidos."));

        if (await currentUserService.ObtenerUsuarioActualIdAsync() is not { } usuarioId)
            return Result.Fallo<Guid>(Error.Crear("Adjunto.SinAutor", "No pudimos identificar quién resuelve el adjunto."));

        var adjunto = await repositorio.ObtenerPorIdAsync(request.AdjuntoId, cancellationToken);
        if (adjunto is null)
            return Result.Fallo<Guid>(Error.Crear("Adjunto.NoEncontrado", "No encontramos este adjunto."));

        var ahora = DateTime.UtcNow;
        TipoEventoConversacion tipoEvento;
        switch (request.Resolucion)
        {
            case ResolucionAdjunto.Rechazar when adjunto.EstaPendiente:
                adjunto.Rechazar(request.Motivo!, usuarioId, ahora);
                tipoEvento = TipoEventoConversacion.AdjuntoRechazado;
                break;
            case ResolucionAdjunto.Descartar when adjunto.EstaPendiente:
                adjunto.Descartar(usuarioId, ahora);
                tipoEvento = TipoEventoConversacion.AdjuntoDescartado;
                break;
            case ResolucionAdjunto.Reabrir when adjunto.EstadoReparto is EstadoRepartoAdjunto.Rechazado or EstadoRepartoAdjunto.Descartado:
                adjunto.Reabrir();
                tipoEvento = TipoEventoConversacion.AdjuntoReabierto;
                break;
            default:
                return Result.Fallo<Guid>(Error.Crear("Adjunto.EstadoNoValido", "Este adjunto no admite esa acción."));
        }

        eventoRepositorio.Agregar(new EventoConversacion(origen.ConversacionId, tipoEvento, adjunto.Id, ahora));
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(adjunto.Id);
    }
}
