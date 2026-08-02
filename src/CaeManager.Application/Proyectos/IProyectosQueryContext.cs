using CaeManager.Domain.Proyectos;

namespace CaeManager.Application.Proyectos;

public interface IProyectosQueryContext
{
    IQueryable<Proyecto> Proyectos { get; }
    IQueryable<ProyectoTecnico> ProyectosTecnicos { get; }
}
