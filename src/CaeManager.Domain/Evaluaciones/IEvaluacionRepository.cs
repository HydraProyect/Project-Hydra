namespace CaeManager.Domain.Evaluaciones;

public interface IEvaluacionRepository
{
    Task<Evaluacion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(Evaluacion evaluacion);
}
