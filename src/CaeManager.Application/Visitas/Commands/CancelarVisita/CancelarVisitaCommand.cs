using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Visitas.Commands.CancelarVisita;

/// <summary>
/// FS-11 (auditoría UX de flujos sin salida, 2026-09-24): cancelar una Visita la
/// deja en estado Cancelada, reversible con <c>ReactivarVisitaCommand</c>. Antes
/// era un borrado lógico sin estado ni restauración, y en Outbound —donde la
/// Visita es la gestión de ingreso ante la plataforma destino— su historia se
/// perdía. El motivo es opcional.
/// </summary>
public record CancelarVisitaCommand(Guid Id, string? Motivo = null) : ICommand;

public class CancelarVisitaCommandValidator : AbstractValidator<CancelarVisitaCommand>
{
    public CancelarVisitaCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Motivo).MaximumLength(Visita.LongitudMaximaMotivo);
    }
}

public class CancelarVisitaCommandHandler(
    IVisitaRepository repositorio, IUnitOfWork unitOfWork, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<CancelarVisitaCommand, Result>
{
    public async Task<Result> Handle(CancelarVisitaCommand request, CancellationToken cancellationToken)
    {
        // Misma regla que al reactivar (AutorizacionCancelacionVisita): alcance de
        // GESTIÓN sobre el Centro; fuera de él se responde igual que "no existe".
        var visita = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (visita is null || !await AutorizacionCancelacionVisita.PuedeGestionarAsync(alcanceDatos, visita, cancellationToken))
            return Result.Fallo(AutorizacionCancelacionVisita.NoEncontrada);

        if (visita.EstaCancelada)
            return Result.Fallo(Error.Crear("Visita.YaCancelada", "Esta visita ya está cancelada."));

        // Quién cancela no se guarda en la Visita: lo registra la auditoría,
        // que separa el Actor real del Usuario simulado.
        visita.Cancelar(DateTime.UtcNow, request.Motivo);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
