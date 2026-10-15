using CaeManager.Domain.Proyectos;

namespace CaeManager.Application.Proyectos;

public interface IProyectosQueryContext
{
    IQueryable<Proyecto> Proyectos { get; }
    IQueryable<ProyectoTecnico> ProyectosTecnicos { get; }
}

/// <summary>Requisitos documentales de los eventos y sus plantillas (ver <see cref="TipoProyecto.Evento"/>).</summary>
public interface IRequisitosEventoQueryContext
{
    IQueryable<ProyectoRequisito> ProyectosRequisitos { get; }
    IQueryable<PlantillaRequisitosEvento> PlantillasRequisitosEvento { get; }
    IQueryable<PlantillaRequisitosEventoTipo> PlantillasRequisitosEventoTipos { get; }
}
