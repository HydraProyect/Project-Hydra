using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class FirmaDigitalDocumentoRepository(CaeManagerDbContext dbContext) : IFirmaDigitalDocumentoRepository
{
    public void Agregar(FirmaDigitalDocumento firma) => dbContext.FirmasDigitalesDocumento.Add(firma);

    public async Task<IReadOnlyList<FirmaDigitalDocumento>> ObtenerPorDocumentoAsync(
        Guid documentoId, CancellationToken cancellationToken = default) =>
        await dbContext.FirmasDigitalesDocumento
            .Where(f => f.DocumentoId == documentoId)
            .OrderBy(f => f.Indice)
            .ToListAsync(cancellationToken);

    public void EliminarDeDocumento(IReadOnlyList<FirmaDigitalDocumento> firmas) =>
        dbContext.FirmasDigitalesDocumento.RemoveRange(firmas);
}
