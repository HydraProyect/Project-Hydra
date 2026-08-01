using CaeManager.Domain.RequisitosDocumentales;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class RequisitoDocumentalRepository(CaeManagerDbContext dbContext) : IRequisitoDocumentalRepository
{
    public Task<RequisitoDocumental?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.RequisitosDocumentales.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public void Agregar(RequisitoDocumental requisito) => dbContext.RequisitosDocumentales.Add(requisito);

    public void Eliminar(RequisitoDocumental requisito) => dbContext.RequisitosDocumentales.Remove(requisito);
}
