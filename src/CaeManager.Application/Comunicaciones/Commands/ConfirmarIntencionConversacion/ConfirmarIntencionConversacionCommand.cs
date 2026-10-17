using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.ConfirmarIntencionConversacion;

public record ConfirmarIntencionConversacionCommand(Guid ConversacionId, IntencionCae Intencion) : ICommand;

public class ConfirmarIntencionConversacionCommandValidator : AbstractValidator<ConfirmarIntencionConversacionCommand>
{
    public ConfirmarIntencionConversacionCommandValidator()
    {
        RuleFor(c => c.ConversacionId).NotEmpty();
        RuleFor(c => c.Intencion).IsInEnum();
    }
}

public class ConfirmarIntencionConversacionCommandHandler(
    IConversacionRepository conversaciones, IIntencionConversacionRepository intenciones,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<ConfirmarIntencionConversacionCommand, Result>
{
    public async Task<Result> Handle(ConfirmarIntencionConversacionCommand request, CancellationToken cancellationToken)
    {
        var conversacion = await conversaciones.ObtenerPorIdAsync(request.ConversacionId, cancellationToken);
        if (conversacion is null || !await alcanceDatos.ConversacionVisibleAsync(conversacion.ClienteId,
            conversacion.EmpresaId, conversacion.ConexionIntegracionId, cancellationToken))
            return Result.Fallo(Error.Crear("Conversacion.NoEncontrada", "No encontramos esta conversación."));
        var intencion = await intenciones.ObtenerPorConversacionIdAsync(request.ConversacionId, cancellationToken);
        if (intencion is null)
            return Result.Fallo(Error.Crear("IntencionConversacion.NoEncontrada", "Esta conversación todavía no tiene una propuesta de intención."));
        intencion.Confirmar(request.Intencion);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
