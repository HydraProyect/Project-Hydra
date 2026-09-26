using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Visitas.Commands.CancelarVisitas;

/// <summary>
/// Cancelación en lote (FS-11). Mismo criterio de éxito parcial que los borrados
/// en lote (ver EliminarClientesCommand): cada Visita que no se pudo cancelar
/// deja un error y no tumba al resto. Devuelve los ids cancelados para que el
/// aviso ofrezca «Deshacer» solo sobre ellos.
/// </summary>
public record CancelarVisitasCommand(IReadOnlyList<Guid> Ids, string? Motivo = null) : ICommand<ResultadoCancelacionLoteDto>;

public record ResultadoCancelacionLoteDto(int Canceladas, IReadOnlyList<string> Errores, IReadOnlyList<Guid> IdsCanceladas);

public class CancelarVisitasCommandValidator : AbstractValidator<CancelarVisitasCommand>
{
    public CancelarVisitasCommandValidator()
    {
        RuleFor(c => c.Ids).NotEmpty();
        RuleFor(c => c.Motivo).MaximumLength(Visita.LongitudMaximaMotivo);
    }
}

public class CancelarVisitasCommandHandler(
    IVisitaRepository repositorio, IUnitOfWork unitOfWork, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<CancelarVisitasCommand, Result<ResultadoCancelacionLoteDto>>
{
    public async Task<Result<ResultadoCancelacionLoteDto>> Handle(CancelarVisitasCommand request, CancellationToken cancellationToken)
    {
        // Alcance de GESTIÓN sobre el Centro de cada Visita, como en
        // CancelarVisitaCommand; se resuelve una vez para todo el lote. Una Visita
        // fuera de la Asignación de Cartera cuenta como una que ya no existía, sin
        // revelar qué hay fuera del alcance. null = sin restricción.
        var centroIdsParaGestion = await alcanceDatos.ObtenerCentroIdsParaGestionAsync(cancellationToken);

        var ahora = DateTime.UtcNow;
        var canceladas = new List<Guid>();
        var errores = new List<string>();

        foreach (var id in request.Ids)
        {
            var visita = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
            if (visita is null || !AutorizacionCancelacionVisita.PuedeGestionar(centroIdsParaGestion, visita))
            {
                errores.Add("Una visita ya no existía.");
                continue;
            }

            if (visita.EstaCancelada)
            {
                errores.Add("Una visita ya estaba cancelada.");
                continue;
            }

            visita.Cancelar(ahora, request.Motivo);
            canceladas.Add(visita.Id);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoCancelacionLoteDto(canceladas.Count, errores, canceladas));
    }
}
