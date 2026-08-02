using CaeManager.Domain.Asignaciones;

namespace CaeManager.Application.Asignaciones;

public interface IAsignacionesQueryContext
{
    IQueryable<Asignacion> Asignaciones { get; }
}
