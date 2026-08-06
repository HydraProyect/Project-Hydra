using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class TipoDocumentoCentroRepository(CaeManagerDbContext dbContext) : ITipoDocumentoCentroRepository
{
    public async Task<TipoDocumentoCentro?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.Set<TipoDocumentoCentro>().FirstOrDefaultAsync(tc => tc.Id == id, cancellationToken);

    public async Task<IReadOnlyList<TipoDocumentoCentro>> ObtenerPorTipoDocumentoAsync(Guid tipoDocumentoId, CancellationToken cancellationToken = default) =>
        await dbContext.Set<TipoDocumentoCentro>().Where(tc => tc.TipoDocumentoId == tipoDocumentoId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TipoDocumentoCentro>> ObtenerPorCentroAsync(Guid centroId, CancellationToken cancellationToken = default) =>
        await dbContext.Set<TipoDocumentoCentro>().Where(tc => tc.CentroId == centroId).ToListAsync(cancellationToken);

    public async Task<TipoDocumentoCentro?> ObtenerPorParAsync(Guid tipoDocumentoId, Guid centroId, CancellationToken cancellationToken = default) =>
        await dbContext.Set<TipoDocumentoCentro>().FirstOrDefaultAsync(
            tc => tc.TipoDocumentoId == tipoDocumentoId && tc.CentroId == centroId, cancellationToken);

    public void Agregar(TipoDocumentoCentro tipoDocumentoCentro) => dbContext.Set<TipoDocumentoCentro>().Add(tipoDocumentoCentro);

    public void Eliminar(TipoDocumentoCentro tipoDocumentoCentro) => dbContext.Set<TipoDocumentoCentro>().Remove(tipoDocumentoCentro);
}
