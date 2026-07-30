using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.AsignarEjecutivoConversacion;

public record AsignarEjecutivoConversacionCommand(Guid ConversacionId, Guid? EjecutivoId) : IRequest<Result>;

public class AsignarEjecutivoConversacionCommandValidator : AbstractValidator<AsignarEjecutivoConversacionCommand>
{
    public AsignarEjecutivoConversacionCommandValidator()
    {
        RuleFor(c => c.ConversacionId).NotEmpty().WithMessage("La asignación debe indicar una conversación.");
    }
}

/// <summary>
/// No carga el Ejecutivo antes de asignarlo: ApplicationUser vive en
/// Infrastructure.Identity, que Application no puede referenciar (mismo
/// motivo documentado en ReasignarEjecutivoClienteCommand) — Web ya valida
/// que el id viene de un selector poblado con UserManager antes de llamar a
/// este Command.
/// </summary>
public class AsignarEjecutivoConversacionCommandHandler(IConversacionCorreoRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<AsignarEjecutivoConversacionCommand, Result>
{
    public async Task<Result> Handle(AsignarEjecutivoConversacionCommand request, CancellationToken cancellationToken)
    {
        var conversacion = await repositorio.ObtenerPorIdAsync(request.ConversacionId, cancellationToken);
        if (conversacion is null)
            return Result.Fallo(Error.Crear("ConversacionCorreo.NoEncontrada", "No encontramos esta conversación."));

        conversacion.Asignar(request.EjecutivoId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
