using CaeManager.Domain.Evaluaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class EvaluacionRepository(CaeManagerDbContext dbContext) : IEvaluacionRepository
{
    public Task<Evaluacion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Evaluaciones.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public void Agregar(Evaluacion evaluacion) => dbContext.Evaluaciones.Add(evaluacion);
}
