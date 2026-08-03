using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Evaluaciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Evaluaciones.Commands.EliminarEvaluaciones;

/// <summary>Borrado en lote — ver EliminarClientesCommand para el criterio de éxito parcial.</summary>
public record EliminarEvaluacionesCommand(IReadOnlyList<Guid> Ids, Guid UsuarioId) : ICommand<ResultadoEliminacionLoteDto>;

public class EliminarEvaluacionesCommandValidator : AbstractValidator<EliminarEvaluacionesCommand>
{
    public EliminarEvaluacionesCommandValidator() => RuleFor(c => c.Ids).NotEmpty();
}

public class EliminarEvaluacionesCommandHandler(IEvaluacionRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarEvaluacionesCommand, Result<ResultadoEliminacionLoteDto>>
{
    public async Task<Result<ResultadoEliminacionLoteDto>> Handle(EliminarEvaluacionesCommand request, CancellationToken cancellationToken)
    {
        var eliminados = 0;
        var errores = new List<string>();

        foreach (var id in request.Ids)
        {
            var evaluacion = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
            if (evaluacion is null)
            {
                errores.Add("Una evaluación ya no existía.");
                continue;
            }

            evaluacion.MarcarComoEliminado(request.UsuarioId);
            eliminados++;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoEliminacionLoteDto(eliminados, errores));
    }
}
