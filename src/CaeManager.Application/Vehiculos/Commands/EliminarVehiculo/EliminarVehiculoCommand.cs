using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Vehiculos;
using MediatR;

namespace CaeManager.Application.Vehiculos.Commands.EliminarVehiculo;

public record EliminarVehiculoCommand(Guid Id, Guid UsuarioId) : ICommand;

public class EliminarVehiculoCommandHandler(IVehiculoRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarVehiculoCommand, Result>
{
    public async Task<Result> Handle(EliminarVehiculoCommand request, CancellationToken cancellationToken)
    {
        var vehiculo = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (vehiculo is null || !await alcanceDatos.VehiculoVisibleAsync(vehiculo.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Vehiculo.NoEncontrado", "No encontramos este vehículo."));

        vehiculo.MarcarComoEliminado(request.UsuarioId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
