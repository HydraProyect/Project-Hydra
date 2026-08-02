using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Trabajadores.Commands.EliminarTrabajadores;

/// <summary>Borrado en lote (P3-31) — ver EliminarClientesCommand para el criterio de éxito parcial.</summary>
public record EliminarTrabajadoresCommand(IReadOnlyList<Guid> Ids, Guid UsuarioId) : ICommand<ResultadoEliminacionLoteDto>;

public class EliminarTrabajadoresCommandValidator : AbstractValidator<EliminarTrabajadoresCommand>
{
    public EliminarTrabajadoresCommandValidator() => RuleFor(c => c.Ids).NotEmpty();
}

public class EliminarTrabajadoresCommandHandler(ITrabajadorRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarTrabajadoresCommand, Result<ResultadoEliminacionLoteDto>>
{
    public async Task<Result<ResultadoEliminacionLoteDto>> Handle(EliminarTrabajadoresCommand request, CancellationToken cancellationToken)
    {
        var eliminados = 0;
        var errores = new List<string>();

        foreach (var id in request.Ids)
        {
            var trabajador = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
            if (trabajador is null || !await alcanceDatos.TrabajadorVisibleAsync(trabajador.Id, cancellationToken))
            {
                errores.Add("Un trabajador ya no existía.");
                continue;
            }

            trabajador.MarcarComoEliminado(request.UsuarioId);
            eliminados++;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoEliminacionLoteDto(eliminados, errores));
    }
}
