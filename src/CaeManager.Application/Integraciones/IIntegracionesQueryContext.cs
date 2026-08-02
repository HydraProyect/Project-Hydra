using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Integraciones;

public interface IIntegracionesQueryContext
{
    IQueryable<ConexionIntegracion> ConexionesIntegracion { get; }
}
