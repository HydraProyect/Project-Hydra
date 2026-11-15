using CaeManager.Domain.Inbound.EvidenciasInbound;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Inbound.Persistence;

/// <summary>
/// Sin <c>Eliminar</c> físico: el rol de runtime no tiene DELETE sobre <c>EvidenciasInbound</c>, y la retención de la
/// copia (DI-11) no está decidida, así que ningún camino de la aplicación la borra.
/// </summary>
public class EvidenciaInboundRepository(CaeManagerDbContext dbContext) : IEvidenciaInboundRepository
{
    public Task<EvidenciaInbound?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.EvidenciasInbound.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public void Agregar(EvidenciaInbound evidencia) => dbContext.EvidenciasInbound.Add(evidencia);
}
