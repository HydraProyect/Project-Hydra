using CaeManager.Application.Common;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.AsignarClienteConversacion;

/// <summary>Resuelve una conversación de la cola de triage (ver § 12.4) asignándole un Cliente real.</summary>
public record AsignarClienteConversacionCommand(Guid ConversacionId, Guid ClienteId) : ICommand;

public class AsignarClienteConversacionCommandValidator : AbstractValidator<AsignarClienteConversacionCommand>
{
    public AsignarClienteConversacionCommandValidator()
    {
        RuleFor(c => c.ConversacionId).NotEmpty().WithMessage("La asignación debe indicar una conversación.");
        RuleFor(c => c.ClienteId).NotEmpty().WithMessage("Debes elegir un cliente.");
    }
}

public class AsignarClienteConversacionCommandHandler(
    IConversacionCorreoRepository conversacionRepositorio, IClienteRepository clienteRepositorio,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<AsignarClienteConversacionCommand, Result>
{
    public async Task<Result> Handle(AsignarClienteConversacionCommand request, CancellationToken cancellationToken)
    {
        var cliente = await clienteRepositorio.ObtenerPorIdAsync(request.ClienteId, cancellationToken);
        // Mismo mensaje que "no existe" a propósito (ver
        // AlcanceDatosServiceExtensions): no se confirma la existencia de un
        // cliente fuera de la cartera de quien pregunta.
        if (cliente is null || !await alcanceDatos.ClienteVisibleAsync(cliente.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Cliente.NoEncontrado", "No encontramos este cliente."));

        var conversacion = await conversacionRepositorio.ObtenerPorIdAsync(request.ConversacionId, cancellationToken);
        if (conversacion is null || !await alcanceDatos.ClienteOpcionalVisibleAsync(conversacion.ClienteId, cancellationToken))
            return Result.Fallo(Error.Crear("ConversacionCorreo.NoEncontrada", "No encontramos esta conversación."));

        conversacion.AsignarCliente(cliente.Id);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
