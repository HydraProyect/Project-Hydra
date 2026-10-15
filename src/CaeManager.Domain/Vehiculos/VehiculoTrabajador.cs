using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;

namespace CaeManager.Domain.Vehiculos;

/// <summary>Trabajador autorizado para conducir o utilizar un Vehículo del mismo Tenant propietario.</summary>
public class VehiculoTrabajador : EntidadConTenant
{
    public Guid VehiculoId { get; private set; }
    public Guid TrabajadorId { get; private set; }
    public Trabajador Trabajador { get; private set; } = null!;

    private VehiculoTrabajador() { }

    public VehiculoTrabajador(Guid vehiculoId, Guid trabajadorId)
    {
        if (vehiculoId == Guid.Empty) throw new ArgumentException("El vehículo es obligatorio.", nameof(vehiculoId));
        if (trabajadorId == Guid.Empty) throw new ArgumentException("El trabajador es obligatorio.", nameof(trabajadorId));
        VehiculoId = vehiculoId;
        TrabajadorId = trabajadorId;
    }
}
