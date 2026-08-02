using CaeManager.Domain.Evaluaciones;

namespace CaeManager.Application.Evaluaciones;

public interface IEvaluacionesQueryContext
{
    IQueryable<Evaluacion> Evaluaciones { get; }
}
