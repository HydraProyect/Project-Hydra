using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Vehiculos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Vehiculos.Commands.EliminarVehiculos;

/// <summary>Borrado en lote — ver EliminarClientesCommand para el criterio de éxito parcial.</summary>
public record EliminarVehiculosCommand(IReadOnlyList<Guid> Ids) : ICommand<ResultadoEliminacionLoteDto>;

public class EliminarVehiculosCommandValidator : AbstractValidator<EliminarVehiculosCommand>
{
    public EliminarVehiculosCommandValidator() => RuleFor(c => c.Ids).NotEmpty();
}

public class EliminarVehiculosCommandHandler(
    IVehiculoRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork,
    ICurrentUserService currentUserService)
    : IRequestHandler<EliminarVehiculosCommand, Result<ResultadoEliminacionLoteDto>>
{
    public async Task<Result<ResultadoEliminacionLoteDto>> Handle(EliminarVehiculosCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<ResultadoEliminacionLoteDto>(Error.Crear("Vehiculo.SinIdentidad", "No se pudo confirmar tu identidad. Vuelve a iniciar sesión e inténtalo de nuevo."));

        var eliminados = 0;
        var errores = new List<string>();

        foreach (var id in request.Ids)
        {
            var vehiculo = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
            if (vehiculo is null || !await alcanceDatos.VehiculoVisibleAsync(vehiculo.Id, cancellationToken))
            {
                errores.Add("Un vehículo ya no existía.");
                continue;
            }

            vehiculo.MarcarComoEliminado(usuarioId.Value);
            eliminados++;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoEliminacionLoteDto(eliminados, errores));
    }
}
