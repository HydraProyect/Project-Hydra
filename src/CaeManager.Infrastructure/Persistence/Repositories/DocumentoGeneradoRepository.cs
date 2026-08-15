using CaeManager.Domain.Plantillas;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class DocumentoGeneradoRepository(CaeManagerDbContext dbContext) : IDocumentoGeneradoRepository
{
    public void Agregar(DocumentoGenerado documentoGenerado) => dbContext.DocumentosGenerados.Add(documentoGenerado);

    public Task<DocumentoGenerado?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.DocumentosGenerados.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
}
