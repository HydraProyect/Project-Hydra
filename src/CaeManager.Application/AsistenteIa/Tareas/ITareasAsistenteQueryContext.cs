using CaeManager.Domain.AsistenteIa;

namespace CaeManager.Application.AsistenteIa.Tareas;

/// <summary>
/// Lectura de las tareas del asistente de flujos. El filtro global limita al
/// Tenant en el que se trabaja; la persona la filtra cada consulta por su
/// Actor real, y RLS lo repite en la base.
/// </summary>
public interface ITareasAsistenteQueryContext
{
    IQueryable<TareaAsistente> TareasAsistente { get; }
    IQueryable<TurnoTareaAsistente> TurnosTareaAsistente { get; }
    IQueryable<PasoTareaAsistente> PasosTareaAsistente { get; }
}
