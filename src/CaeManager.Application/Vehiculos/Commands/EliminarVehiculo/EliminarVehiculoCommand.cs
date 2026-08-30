using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Vehiculos;
using MediatR;

namespace CaeManager.Application.Vehiculos.Commands.EliminarVehiculo;

public record EliminarVehiculoCommand(Guid Id) : ICommand;

public class EliminarVehiculoCommandHandler(
    IVehiculoRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork,
    ICurrentUserService currentUserService)
    : IRequestHandler<EliminarVehiculoCommand, Result>
{
    public async Task<Result> Handle(EliminarVehiculoCommand request, CancellationToken cancellationToken)
    {
        var vehiculo = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (vehiculo is null || !await alcanceDatos.VehiculoVisibleAsync(vehiculo.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Vehiculo.NoEncontrado", "No encontramos este vehículo."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("Vehiculo.SinIdentidad", "No se pudo confirmar tu identidad. Vuelve a iniciar sesión e inténtalo de nuevo."));

        vehiculo.MarcarComoEliminado(usuarioId.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
