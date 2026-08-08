using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class AcreditacionDocumentoPlataformaRepository(CaeManagerDbContext dbContext) : IAcreditacionDocumentoPlataformaRepository
{
    public Task<AcreditacionDocumentoPlataforma?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.AcreditacionesDocumentoPlataforma
            .Include(a => a.HistorialRechazos)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public void Agregar(AcreditacionDocumentoPlataforma acreditacion) => dbContext.AcreditacionesDocumentoPlataforma.Add(acreditacion);
}
