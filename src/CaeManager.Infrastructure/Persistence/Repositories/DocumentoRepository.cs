using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class DocumentoRepository(CaeManagerDbContext dbContext) : IDocumentoRepository
{
    public Task<Documento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Documentos.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    public void Agregar(Documento documento) => dbContext.Documentos.Add(documento);
}
