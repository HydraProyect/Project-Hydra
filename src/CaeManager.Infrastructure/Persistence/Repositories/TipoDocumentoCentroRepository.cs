using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class TipoDocumentoCentroRepository(CaeManagerDbContext dbContext) : ITipoDocumentoCentroRepository
{
    public async Task<IReadOnlyList<TipoDocumentoCentro>> ObtenerPorTipoDocumentoAsync(Guid tipoDocumentoId, CancellationToken cancellationToken = default) =>
        await dbContext.Set<TipoDocumentoCentro>().Where(tc => tc.TipoDocumentoId == tipoDocumentoId).ToListAsync(cancellationToken);

    public void Agregar(TipoDocumentoCentro tipoDocumentoCentro) => dbContext.Set<TipoDocumentoCentro>().Add(tipoDocumentoCentro);

    public void Eliminar(TipoDocumentoCentro tipoDocumentoCentro) => dbContext.Set<TipoDocumentoCentro>().Remove(tipoDocumentoCentro);
}
