using CaeManager.Application.Common;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.AsignarClienteConversacion;

/// <summary>Resuelve una conversación de la cola de triage (ver § 12.4) asignándole un Cliente real.</summary>
public record AsignarClienteConversacionCommand(Guid ConversacionId, Guid ClienteId) : IRequest<Result>;

public class AsignarClienteConversacionCommandValidator : AbstractValidator<AsignarClienteConversacionCommand>
{
    public AsignarClienteConversacionCommandValidator()
    {
        RuleFor(c => c.ConversacionId).NotEmpty().WithMessage("La asignación debe indicar una conversación.");
        RuleFor(c => c.ClienteId).NotEmpty().WithMessage("Debes elegir un cliente.");
    }
}

public class AsignarClienteConversacionCommandHandler(
    IConversacionCorreoRepository conversacionRepositorio, IClienteRepository clienteRepositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<AsignarClienteConversacionCommand, Result>
{
    public async Task<Result> Handle(AsignarClienteConversacionCommand request, CancellationToken cancellationToken)
    {
        var cliente = await clienteRepositorio.ObtenerPorIdAsync(request.ClienteId, cancellationToken);
        if (cliente is null)
            return Result.Fallo(Error.Crear("Cliente.NoEncontrado", "No encontramos este cliente."));

        var conversacion = await conversacionRepositorio.ObtenerPorIdAsync(request.ConversacionId, cancellationToken);
        if (conversacion is null)
            return Result.Fallo(Error.Crear("ConversacionCorreo.NoEncontrada", "No encontramos esta conversación."));

        conversacion.AsignarCliente(cliente.Id);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
