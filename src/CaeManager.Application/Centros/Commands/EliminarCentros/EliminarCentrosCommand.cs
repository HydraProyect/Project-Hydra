using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Centros.Commands.EliminarCentros;

/// <summary>Borrado en lote — ver EliminarClientesCommand para el criterio de éxito parcial.</summary>
public record EliminarCentrosCommand(IReadOnlyList<Guid> Ids, Guid UsuarioId) : ICommand<ResultadoEliminacionLoteDto>;

public class EliminarCentrosCommandValidator : AbstractValidator<EliminarCentrosCommand>
{
    public EliminarCentrosCommandValidator() => RuleFor(c => c.Ids).NotEmpty();
}

public class EliminarCentrosCommandHandler(
    ICentroRepository repositorio,
    IAsignacionRepository asignaciones,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarCentrosCommand, Result<ResultadoEliminacionLoteDto>>
{
    public async Task<Result<ResultadoEliminacionLoteDto>> Handle(EliminarCentrosCommand request, CancellationToken cancellationToken)
    {
        var eliminados = 0;
        var errores = new List<string>();

        foreach (var id in request.Ids)
        {
            var centro = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
            if (centro is null || !await alcanceDatos.CentroVisibleAsync(centro.Id, cancellationToken))
            {
                errores.Add("Un centro ya no existía.");
                continue;
            }

            centro.MarcarComoEliminado(request.UsuarioId);
            await CierreDeAsignaciones.PorCentroEliminadoAsync(asignaciones, centro.Id, cancellationToken);
            eliminados++;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoEliminacionLoteDto(eliminados, errores));
    }
}
