using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Vehiculos.Commands.RestaurarVehiculo;

/// <summary>
/// «Deshacer» de <c>EliminarVehiculoCommand</c> y de su lote — ver RestaurarClienteCommand para el
/// razonamiento completo del IgnoreQueryFilters()+TenantId. La baja solo marca el Vehículo: sus
/// documentos no se tocan, así que restaurarlo lo devuelve entero.
/// </summary>
public record RestaurarVehiculoCommand(Guid Id) : ICommand;

public class RestaurarVehiculoCommandHandler(
    IVehiculosQueryContext vehiculosContext, ITenantActual tenantActual,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<RestaurarVehiculoCommand, Result>
{
    public async Task<Result> Handle(RestaurarVehiculoCommand request, CancellationToken cancellationToken)
    {
        var vehiculo = await vehiculosContext.Vehiculos
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(v => v.Id == request.Id && v.TenantId == tenantActual.TenantId, cancellationToken);

        if (vehiculo is null || !vehiculo.EstaEliminado)
            return Result.Fallo(Error.Crear("Vehiculo.NoEncontrado", "No encontramos este vehículo eliminado."));

        // Misma autoridad que la baja: VehiculoVisibleAsync es «su Empresa o su Subcontrata es
        // visible», pero su lista pasa por el filtro global de soft delete y excluye la fila que se
        // está restaurando — el titular persistido (EmpresaId/SubcontrataId) es la coordenada estable.
        var titularVisible =
            (vehiculo.EmpresaId is { } empresaId && await alcanceDatos.EmpresaVisibleAsync(empresaId, cancellationToken))
            || (vehiculo.SubcontrataId is { } subcontrataId && await alcanceDatos.SubcontrataVisibleAsync(subcontrataId, cancellationToken));

        if (!titularVisible)
            return Result.Fallo(Error.Crear("Vehiculo.NoEncontrado", "No encontramos este vehículo eliminado."));

        vehiculo.Restaurar();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
