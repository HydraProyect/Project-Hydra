using CaeManager.Domain.Incidencias;

namespace CaeManager.Application.Incidencias;

public interface IIncidenciasQueryContext
{
    IQueryable<Incidencia> Incidencias { get; }
}
