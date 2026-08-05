using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Visitas.Commands.EliminarVisitas;

/// <summary>Borrado en lote — ver EliminarClientesCommand para el criterio de éxito parcial.</summary>
public record EliminarVisitasCommand(IReadOnlyList<Guid> Ids, Guid UsuarioId) : ICommand<ResultadoEliminacionLoteDto>;

public class EliminarVisitasCommandValidator : AbstractValidator<EliminarVisitasCommand>
{
    public EliminarVisitasCommandValidator() => RuleFor(c => c.Ids).NotEmpty();
}

public class EliminarVisitasCommandHandler(IVisitaRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarVisitasCommand, Result<ResultadoEliminacionLoteDto>>
{
    public async Task<Result<ResultadoEliminacionLoteDto>> Handle(EliminarVisitasCommand request, CancellationToken cancellationToken)
    {
        var eliminados = 0;
        var errores = new List<string>();

        foreach (var id in request.Ids)
        {
            var visita = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
            if (visita is null)
            {
                errores.Add("Una visita ya no existía.");
                continue;
            }

            visita.MarcarComoEliminado(request.UsuarioId);
            eliminados++;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoEliminacionLoteDto(eliminados, errores));
    }
}
