using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class RevisionIaDocumentoRepository(CaeManagerDbContext dbContext) : IRevisionIaDocumentoRepository
{
    public Task<RevisionIaDocumento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.RevisionesIaDocumento.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public void Agregar(RevisionIaDocumento revision) => dbContext.RevisionesIaDocumento.Add(revision);
}
