using CaeManager.Application.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.ResponderConversacion;

public record ResponderConversacionCommand(Guid ConversacionId, string CuerpoHtml) : IRequest<Result>;

public class ResponderConversacionCommandValidator : AbstractValidator<ResponderConversacionCommand>
{
    public ResponderConversacionCommandValidator()
    {
        RuleFor(c => c.ConversacionId).NotEmpty().WithMessage("La respuesta debe pertenecer a una conversación.");
        RuleFor(c => c.CuerpoHtml).NotEmpty().WithMessage("La respuesta no puede estar vacía.");
    }
}

/// <summary>
/// Agrega el mensaje Saliente al hilo pero NO lo envía por ningún canal
/// real — este vertical slice no tiene todavía un adaptador
/// MicrosoftGraphIntegrationProvider con CorreoBidireccional (ver
/// ARQUITECTURA-INTEGRACIONES.md § 12.6), así que "responder" solo persiste
/// el mensaje en Hydra. El remitente es un valor simulado hasta que exista
/// ConexionIntegracion.ClienteId (§ 12.2) para resolver el buzón real del
/// Cliente/Tenant que atiende la conversación.
/// </summary>
public class ResponderConversacionCommandHandler(IConversacionCorreoRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<ResponderConversacionCommand, Result>
{
    private const string RemitenteSimuladoEmail = "equipo-cae@buzon-simulado.local";

    public async Task<Result> Handle(ResponderConversacionCommand request, CancellationToken cancellationToken)
    {
        var conversacion = await repositorio.ObtenerPorIdAsync(request.ConversacionId, cancellationToken);
        if (conversacion is null)
            return Result.Fallo(Error.Crear("ConversacionCorreo.NoEncontrada", "No encontramos esta conversación."));

        conversacion.AgregarMensaje(DireccionMensaje.Saliente, RemitenteSimuladoEmail, request.CuerpoHtml);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
