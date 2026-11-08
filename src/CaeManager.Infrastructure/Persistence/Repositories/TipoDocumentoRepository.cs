using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class TipoDocumentoRepository(CaeManagerDbContext dbContext) : ITipoDocumentoRepository
{
    public Task<TipoDocumento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.TiposDocumento.Include(t => t.Aliases).FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public async Task<TipoDocumento?> ObtenerPorIdActualizadoAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tipo = (await dbContext.ListarActualesAsync(
            dbContext.TiposDocumento.Include(t => t.Aliases), t => t.Id == id, cancellationToken)).FirstOrDefault();
        // La relectura del tipo no repone sus alias: sin esto, editar un tipo con un alias que otra operación borró
        // intentaría borrarlo otra vez y fallaría con un conflicto de concurrencia.
        if (tipo is not null)
            await dbContext.RecargarColeccionAsync(tipo, t => t.Aliases, cancellationToken);
        return tipo;
    }

    public Task<bool> ExisteConNombreAsync(string nombre, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
        dbContext.TiposDocumento.AnyAsync(
            t => t.Nombre == nombre && (excluirId == null || t.Id != excluirId),
            cancellationToken);

    public void Agregar(TipoDocumento tipoDocumento) => dbContext.TiposDocumento.Add(tipoDocumento);
}
