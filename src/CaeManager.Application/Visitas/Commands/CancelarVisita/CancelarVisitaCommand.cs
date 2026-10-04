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
/// <para>
/// Devuelve el recibo de la cancelación (<see cref="VisitaCanceladaDto"/>): la versión en la que
/// dejó la Visita es lo que el aviso «Deshacer» devuelve a <c>ReactivarVisitaCommand</c>, que
/// rechaza si la Visita cambió desde entonces.
/// </para>
/// </summary>
public record CancelarVisitaCommand(Guid Id, string? Motivo = null) : ICommand<VisitaCanceladaDto>;

/// <summary>Recibo de una cancelación: qué Visita y en qué versión quedó. Lo calcula el handler, no la pantalla.</summary>
public record VisitaCanceladaDto(Guid Id, Guid VersionResultante);

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
    : IRequestHandler<CancelarVisitaCommand, Result<VisitaCanceladaDto>>
{
    public async Task<Result<VisitaCanceladaDto>> Handle(CancelarVisitaCommand request, CancellationToken cancellationToken)
    {
        // Misma regla que al reactivar (AutorizacionCancelacionVisita): alcance de
        // GESTIÓN sobre el Centro; fuera de él se responde igual que "no existe".
        var visita = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (visita is null || !await AutorizacionCancelacionVisita.PuedeGestionarAsync(alcanceDatos, visita, cancellationToken))
            return Result.Fallo<VisitaCanceladaDto>(AutorizacionCancelacionVisita.NoEncontrada);

        if (visita.EstaCancelada)
            return Result.Fallo<VisitaCanceladaDto>(Error.Crear("Visita.YaCancelada", "Esta visita ya está cancelada."));

        // Quién cancela no se guarda en la Visita: lo registra la auditoría,
        // que separa el Actor real del Usuario simulado.
        visita.Cancelar(DateTime.UtcNow, request.Motivo);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new VisitaCanceladaDto(visita.Id, visita.Version));
    }
}
