using CaeManager.Domain.Importacion;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class HistorialImportacionRepository(CaeManagerDbContext dbContext) : IHistorialImportacionRepository
{
    public void Agregar(HistorialImportacion registro) => dbContext.HistorialImportaciones.Add(registro);
}
