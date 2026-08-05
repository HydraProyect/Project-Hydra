using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Subcontratas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Subcontratas.Commands.EliminarSubcontratas;

/// <summary>Borrado en lote — ver EliminarClientesCommand para el criterio de éxito parcial.</summary>
public record EliminarSubcontratasCommand(IReadOnlyList<Guid> Ids, Guid UsuarioId) : ICommand<ResultadoEliminacionLoteDto>;

public class EliminarSubcontratasCommandValidator : AbstractValidator<EliminarSubcontratasCommand>
{
    public EliminarSubcontratasCommandValidator() => RuleFor(c => c.Ids).NotEmpty();
}

public class EliminarSubcontratasCommandHandler(ISubcontrataRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarSubcontratasCommand, Result<ResultadoEliminacionLoteDto>>
{
    public async Task<Result<ResultadoEliminacionLoteDto>> Handle(EliminarSubcontratasCommand request, CancellationToken cancellationToken)
    {
        var eliminados = 0;
        var errores = new List<string>();

        foreach (var id in request.Ids)
        {
            var subcontrata = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
            if (subcontrata is null || !await alcanceDatos.SubcontrataVisibleAsync(subcontrata.Id, cancellationToken))
            {
                errores.Add("Una subcontrata ya no existía.");
                continue;
            }

            if (await repositorio.TieneTrabajadoresAsync(id, cancellationToken))
            {
                errores.Add($"{subcontrata.RazonSocial}: tiene trabajadores.");
                continue;
            }

            subcontrata.MarcarComoEliminado(request.UsuarioId);
            eliminados++;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoEliminacionLoteDto(eliminados, errores));
    }
}
