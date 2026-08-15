using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class FirmaEnCampoDocumentoRepository(CaeManagerDbContext dbContext) : IFirmaEnCampoDocumentoRepository
{
    public void Agregar(FirmaEnCampoDocumento firma) => dbContext.FirmasEnCampoDocumento.Add(firma);

    public async Task<IReadOnlyList<FirmaEnCampoDocumento>> ObtenerPorDocumentoAsync(
        Guid documentoId, CancellationToken cancellationToken = default) =>
        await dbContext.FirmasEnCampoDocumento
            .Where(f => f.DocumentoId == documentoId)
            .OrderBy(f => f.FirmadoEnUtc)
            .ToListAsync(cancellationToken);
}
