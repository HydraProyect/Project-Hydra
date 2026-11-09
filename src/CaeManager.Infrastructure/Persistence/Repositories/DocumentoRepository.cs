using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class DocumentoRepository(CaeManagerDbContext dbContext) : IDocumentoRepository
{
    public Task<Documento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Documentos.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    // Al retirar el filtro combinado (Tenant y eliminados) se repone a mano el Tenant propietario. RLS sigue aplicándose.
    public async Task<bool> PodriaExistirAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.TenantPropietarioId is not { } tenantId
        || await dbContext.Documentos.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(d => d.Id == id && d.TenantId == tenantId, cancellationToken);

    public void Agregar(Documento documento) => dbContext.Documentos.Add(documento);
}
