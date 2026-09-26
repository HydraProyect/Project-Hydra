namespace CaeManager.Domain.AsistenteIa;

public interface ITareaAsistenteRepository
{
    void Agregar(TareaAsistente tarea);

    /// <summary>
    /// La tarea con sus turnos y pasos, solo si pertenece a esa persona. El
    /// filtro por persona se repite aquí aunque RLS ya lo aplique: la consulta
    /// no debe depender de que la política exista para ser correcta.
    /// </summary>
    Task<TareaAsistente?> ObtenerDePersonaAsync(Guid tareaId, Guid actorRealUsuarioId, CancellationToken cancellationToken = default);
}
