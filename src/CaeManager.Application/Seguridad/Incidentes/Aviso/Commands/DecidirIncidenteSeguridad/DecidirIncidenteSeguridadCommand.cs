using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Seguridad.Incidentes.Aviso.Commands.DecidirIncidenteSeguridad;

/// <summary>
/// «Necesito más tiempo para evaluarlo» (<see cref="DecisionIncidenteSeguridad.Evaluar"/>) o «Descartar (falsa
/// alarma)» con motivo (<see cref="DecisionIncidenteSeguridad.Descartar"/>). Ninguna de las dos envía nada ni
/// empieza el plazo. Confirmar no va por aquí: confirma el clic que envía el primer aviso
/// (<see cref="ConfirmarYEnviarPrimerAviso.ConfirmarYEnviarPrimerAvisoCommand"/>).
/// </summary>
public record DecidirIncidenteSeguridadCommand(Guid IncidenteId, DecisionIncidenteSeguridad Decision, string? Motivo) : ICommand;

public class DecidirIncidenteSeguridadCommandValidator : AbstractValidator<DecidirIncidenteSeguridadCommand>
{
    public DecidirIncidenteSeguridadCommandValidator()
    {
        RuleFor(c => c.IncidenteId).NotEmpty().WithMessage("Indica el incidente.");
        RuleFor(c => c.Decision)
            .Must(d => d is DecisionIncidenteSeguridad.Evaluar or DecisionIncidenteSeguridad.Descartar)
            .WithMessage("Para confirmar el incidente, envía el primer aviso.");
        RuleFor(c => c.Motivo)
            .Must(m => !string.IsNullOrWhiteSpace(m)).When(c => c.Decision == DecisionIncidenteSeguridad.Descartar)
            .WithMessage("Indica el motivo por el que lo descartas.")
            .MaximumLength(TransicionIncidenteSeguridad.LongitudMaximaMotivo)
            .WithMessage($"El motivo no puede superar {TransicionIncidenteSeguridad.LongitudMaximaMotivo} caracteres.");
    }
}

public class DecidirIncidenteSeguridadCommandHandler(
    ICurrentUserService currentUserService,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    IAutorizacionAdminPlataforma autorizacion,
    IIncidentesSeguridadPlataforma plataforma)
    : IRequestHandler<DecidirIncidenteSeguridadCommand, Result>
{
    public async Task<Result> Handle(DecidirIncidenteSeguridadCommand request, CancellationToken cancellationToken)
    {
        if (await AutorizacionAvisoIncidenteSeguridad.MotivoParaNoDecidirAsync(
                currentUserService, sesionPrivilegiadaActual, autorizacion, cancellationToken) is { } motivo)
            return Result.Fallo(motivo);

        switch (request.Decision)
        {
            case DecisionIncidenteSeguridad.Evaluar:
                return await plataforma.DecidirAsync(request.IncidenteId, request.Decision, motivo: null, cancellationToken);
            case DecisionIncidenteSeguridad.Descartar:
                if (string.IsNullOrWhiteSpace(request.Motivo))
                    return Result.Fallo(ErroresIncidenteSeguridad.MotivoObligatorio);
                return await plataforma.DecidirAsync(request.IncidenteId, request.Decision, request.Motivo.Trim(), cancellationToken);
            default:
                return Result.Fallo(ErroresIncidenteSeguridad.EstadoNoValido);
        }
    }
}
