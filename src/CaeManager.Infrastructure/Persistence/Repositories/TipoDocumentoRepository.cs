using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class TipoDocumentoRepository(CaeManagerDbContext dbContext) : ITipoDocumentoRepository
{
    public Task<TipoDocumento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.TiposDocumento.Include(t => t.Aliases).FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public Task<bool> ExisteConNombreAsync(string nombre, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        dbContext.TiposDocumento.AnyAsync(
            t => t.Nombre == nombre && (excluirId == null || t.Id != excluirId),
            cancellationToken);

    public void Agregar(TipoDocumento tipoDocumento) => dbContext.TiposDocumento.Add(tipoDocumento);
}
