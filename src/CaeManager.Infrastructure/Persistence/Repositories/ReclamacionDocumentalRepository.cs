using CaeManager.Domain.Reclamaciones;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ReclamacionDocumentalRepository(CaeManagerDbContext dbContext) : IReclamacionDocumentalRepository
{
    public void Agregar(ReclamacionDocumental reclamacion) => dbContext.ReclamacionesDocumentales.Add(reclamacion);
}
