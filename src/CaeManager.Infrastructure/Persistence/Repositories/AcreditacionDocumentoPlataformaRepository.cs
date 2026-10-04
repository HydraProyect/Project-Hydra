using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class AcreditacionDocumentoPlataformaRepository(CaeManagerDbContext dbContext) : IAcreditacionDocumentoPlataformaRepository
{
    public Task<AcreditacionDocumentoPlataforma?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.AcreditacionesDocumentoPlataforma
            .Include(a => a.HistorialRechazos)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public async Task<AcreditacionDocumentoPlataforma?> ObtenerPorIdActualizadoAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var acreditacion = await ObtenerPorIdAsync(id, cancellationToken);
        if (acreditacion is not null)
        {
            // Una consulta no refresca una entidad ya rastreada: sin esto se devolvería la copia vieja.
            var entrada = dbContext.Entry(acreditacion);
            await entrada.ReloadAsync(cancellationToken);
            await entrada.Collection(a => a.HistorialRechazos).LoadAsync(cancellationToken);
        }

        return acreditacion;
    }

    public async Task<IReadOnlyList<AcreditacionDocumentoPlataforma>> ObtenerPorDocumentoIdAsync(Guid documentoId, CancellationToken cancellationToken = default) =>
        await dbContext.AcreditacionesDocumentoPlataforma
            .Where(a => a.DocumentoId == documentoId)
            .ToListAsync(cancellationToken);

    public void Agregar(AcreditacionDocumentoPlataforma acreditacion) => dbContext.AcreditacionesDocumentoPlataforma.Add(acreditacion);
}
