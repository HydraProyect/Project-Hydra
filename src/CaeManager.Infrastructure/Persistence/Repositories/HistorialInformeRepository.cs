using CaeManager.Domain.Reportes;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class HistorialInformeRepository(CaeManagerDbContext dbContext) : IHistorialInformeRepository
{
    public void Agregar(HistorialInforme registro) => dbContext.HistorialInformes.Add(registro);
}
