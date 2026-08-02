using CaeManager.Domain.Vehiculos;

namespace CaeManager.Application.Vehiculos;

public interface IVehiculosQueryContext
{
    IQueryable<Vehiculo> Vehiculos { get; }
}
