using CaeManager.Application.Proyectos;
using CaeManager.Domain.Proyectos;

namespace CaeManager.Application.Tests.Proyectos;

public class ProyectosQueryContextFalso : IProyectosQueryContext, IRequisitosEventoQueryContext
{
    public List<Proyecto> ListaProyectos { get; } = [];
    public List<ProyectoTecnico> ListaProyectosTecnicos { get; } = [];

    public List<ProyectoRequisito> ListaProyectosRequisitos { get; } = [];
    public List<PlantillaRequisitosEvento> ListaPlantillas { get; } = [];

    public IQueryable<Proyecto> Proyectos => ListaProyectos.AsQueryable();
    public IQueryable<ProyectoTecnico> ProyectosTecnicos => ListaProyectosTecnicos.AsQueryable();
    public IQueryable<ProyectoRequisito> ProyectosRequisitos => ListaProyectosRequisitos.AsQueryable();
    public IQueryable<PlantillaRequisitosEvento> PlantillasRequisitosEvento => ListaPlantillas.AsQueryable();
    public IQueryable<PlantillaRequisitosEventoTipo> PlantillasRequisitosEventoTipos => ListaPlantillas.SelectMany(p => p.Tipos).AsQueryable();
}
