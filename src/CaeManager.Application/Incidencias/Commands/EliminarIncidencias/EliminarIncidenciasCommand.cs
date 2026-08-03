using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Incidencias;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Incidencias.Commands.EliminarIncidencias;

/// <summary>Borrado en lote — ver EliminarClientesCommand para el criterio de éxito parcial.</summary>
public record EliminarIncidenciasCommand(IReadOnlyList<Guid> Ids, Guid UsuarioId) : ICommand<ResultadoEliminacionLoteDto>;

public class EliminarIncidenciasCommandValidator : AbstractValidator<EliminarIncidenciasCommand>
{
    public EliminarIncidenciasCommandValidator() => RuleFor(c => c.Ids).NotEmpty();
}

public class EliminarIncidenciasCommandHandler(IIncidenciaRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarIncidenciasCommand, Result<ResultadoEliminacionLoteDto>>
{
    public async Task<Result<ResultadoEliminacionLoteDto>> Handle(EliminarIncidenciasCommand request, CancellationToken cancellationToken)
    {
        var eliminados = 0;
        var errores = new List<string>();

        foreach (var id in request.Ids)
        {
            var incidencia = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
            if (incidencia is null)
            {
                errores.Add("Una incidencia ya no existía.");
                continue;
            }

            incidencia.MarcarComoEliminado(request.UsuarioId);
            eliminados++;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoEliminacionLoteDto(eliminados, errores));
    }
}
