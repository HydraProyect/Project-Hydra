using CaeManager.Domain.Trabajadores;

namespace CaeManager.Application.Trabajadores;

public interface ITrabajadoresQueryContext
{
    IQueryable<Trabajador> Trabajadores { get; }
    IQueryable<DeteccionTrabajador> DeteccionesTrabajador { get; }
}
