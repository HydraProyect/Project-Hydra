using CaeManager.Application.Proyectos;
using CaeManager.Domain.Proyectos;

namespace CaeManager.Application.Tests.Proyectos;

public class ProyectosQueryContextFalso : IProyectosQueryContext, IRequisitosEventoQueryContext
{
    public List<Proyecto> ListaProyectos { get; } = [];
    public List<ProyectoTecnico> ListaProyectosTecnicos { get; } = [];

    public List<ProyectoRequisito> ListaProyectosRequisitos { get; } = [];
    public List<PlantillaRequisitosEvento> ListaPlantillas { get; } = [];

    public IQueryable<Proyecto> Proyectos => new CaeManager.Application.Tests.Integraciones.TestAsyncQueryable<Proyecto>(ListaProyectos.AsQueryable());
    public IQueryable<ProyectoTecnico> ProyectosTecnicos => new CaeManager.Application.Tests.Integraciones.TestAsyncQueryable<ProyectoTecnico>(ListaProyectosTecnicos.AsQueryable());
    public IQueryable<ProyectoRequisito> ProyectosRequisitos => new CaeManager.Application.Tests.Integraciones.TestAsyncQueryable<ProyectoRequisito>(ListaProyectosRequisitos.AsQueryable());
    public IQueryable<PlantillaRequisitosEvento> PlantillasRequisitosEvento => ListaPlantillas.AsQueryable();
    public IQueryable<PlantillaRequisitosEventoTipo> PlantillasRequisitosEventoTipos => ListaPlantillas.SelectMany(p => p.Tipos).AsQueryable();
}
