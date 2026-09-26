using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using CaeManager.Application.Visitas.Antelacion;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Visitas.Commands.ReactivarVisita;

/// <summary>
/// Deshace la cancelación de una Visita (FS-11) y la devuelve al estado previo:
/// activa o finalizada según sus fechas, que cancelar no tocó. El motivo es
/// opcional. La auditoría registra el Actor real, el Usuario simulado si hay
/// impersonación, la fecha y el motivo (<see cref="Visita.MotivoReactivacion"/>).
/// <para>
/// Quién puede reactivar es exactamente quien puede cancelar (decisión de la
/// coordinadora, 2026-09-26): la misma <see cref="AutorizacionCancelacionVisita"/>.
/// </para>
/// </summary>
public record ReactivarVisitaCommand(Guid Id, string? Motivo = null) : ICommand;

public class ReactivarVisitaCommandValidator : AbstractValidator<ReactivarVisitaCommand>
{
    public ReactivarVisitaCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Motivo).MaximumLength(Visita.LongitudMaximaMotivo);
    }
}

public class ReactivarVisitaCommandHandler(
    IVisitaRepository repositorio, IUnitOfWork unitOfWork, IAlcanceDatosService alcanceDatos,
    IEvaluadorExpedienteVisitaService evaluadorExpediente, ILogger<ReactivarVisitaCommandHandler> logger)
    : IRequestHandler<ReactivarVisitaCommand, Result>
{
    public async Task<Result> Handle(ReactivarVisitaCommand request, CancellationToken cancellationToken)
    {
        var visita = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (visita is null || !await AutorizacionCancelacionVisita.PuedeGestionarAsync(alcanceDatos, visita, cancellationToken))
            return Result.Fallo(AutorizacionCancelacionVisita.NoEncontrada);

        if (!visita.EstaCancelada)
            return Result.Fallo(Error.Crear("Visita.NoCancelada", "Esta visita no está cancelada."));

        visita.Reactivar(DateTime.UtcNow, request.Motivo);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Mientras estaba cancelada el evaluador la saltaba: si su documentación
        // se completó entretanto, el expediente se sella ahora y no al siguiente
        // cambio de un Documento. Mismo criterio que EditarVisitaCommand.
        try
        {
            await evaluadorExpediente.EvaluarAsync(visita.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo evaluar el expediente documental de la visita {VisitaId}.", visita.Id);
        }

        return Result.Exito();
    }
}
