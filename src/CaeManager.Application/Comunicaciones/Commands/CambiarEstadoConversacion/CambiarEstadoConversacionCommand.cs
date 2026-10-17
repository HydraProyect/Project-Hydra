using CaeManager.Application.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.CambiarEstadoConversacion;

public record CambiarEstadoConversacionCommand(Guid ConversacionId, EstadoConversacion NuevoEstado) : ICommand;

public class CambiarEstadoConversacionCommandValidator : AbstractValidator<CambiarEstadoConversacionCommand>
{
    public CambiarEstadoConversacionCommandValidator()
    {
        RuleFor(c => c.ConversacionId).NotEmpty().WithMessage("El cambio de estado debe indicar una conversación.");
        RuleFor(c => c.NuevoEstado).IsInEnum();
    }
}

public class CambiarEstadoConversacionCommandHandler(
    IConversacionRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<CambiarEstadoConversacionCommand, Result>
{
    public async Task<Result> Handle(CambiarEstadoConversacionCommand request, CancellationToken cancellationToken)
    {
        var conversacion = await repositorio.ObtenerPorIdAsync(request.ConversacionId, cancellationToken);
        // Ver AsignarEjecutivoConversacionCommandHandler (hallazgo N-3).
        if (conversacion is null || !await alcanceDatos.ConversacionVisibleAsync(conversacion.ClienteId, conversacion.EmpresaId, conversacion.ConexionIntegracionId, cancellationToken))
            return Result.Fallo(Error.Crear("Conversacion.NoEncontrada", "No encontramos esta conversación."));

        // La máquina de estados vive en el dominio (TransicionesEstadoConversacion): una transición no permitida vuelve
        // como fallo legible y no llega a guardarse.
        var cambio = conversacion.CambiarEstado(request.NuevoEstado);
        if (cambio.EsFallido)
            return cambio;

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
