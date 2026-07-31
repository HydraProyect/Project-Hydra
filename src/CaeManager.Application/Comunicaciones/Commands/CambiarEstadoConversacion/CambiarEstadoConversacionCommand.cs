using CaeManager.Application.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.CambiarEstadoConversacion;

public record CambiarEstadoConversacionCommand(Guid ConversacionId, EstadoConversacion NuevoEstado) : IRequest<Result>;

public class CambiarEstadoConversacionCommandValidator : AbstractValidator<CambiarEstadoConversacionCommand>
{
    public CambiarEstadoConversacionCommandValidator()
    {
        RuleFor(c => c.ConversacionId).NotEmpty().WithMessage("El cambio de estado debe indicar una conversación.");
        RuleFor(c => c.NuevoEstado).IsInEnum();
    }
}

public class CambiarEstadoConversacionCommandHandler(
    IConversacionCorreoRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<CambiarEstadoConversacionCommand, Result>
{
    public async Task<Result> Handle(CambiarEstadoConversacionCommand request, CancellationToken cancellationToken)
    {
        var conversacion = await repositorio.ObtenerPorIdAsync(request.ConversacionId, cancellationToken);
        // Ver AsignarEjecutivoConversacionCommandHandler (hallazgo N-3).
        if (conversacion is null || !await alcanceDatos.ClienteOpcionalVisibleAsync(conversacion.ClienteId, cancellationToken))
            return Result.Fallo(Error.Crear("ConversacionCorreo.NoEncontrada", "No encontramos esta conversación."));

        conversacion.CambiarEstado(request.NuevoEstado);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
