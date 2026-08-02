using CaeManager.Application.Proyectos;
using CaeManager.Domain.Proyectos;

namespace CaeManager.Application.Tests.Proyectos;

public class ProyectosQueryContextFalso : IProyectosQueryContext
{
    public List<Proyecto> ListaProyectos { get; } = [];
    public List<ProyectoTecnico> ListaProyectosTecnicos { get; } = [];

    public IQueryable<Proyecto> Proyectos => ListaProyectos.AsQueryable();
    public IQueryable<ProyectoTecnico> ProyectosTecnicos => ListaProyectosTecnicos.AsQueryable();
}
