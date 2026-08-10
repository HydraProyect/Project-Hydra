using CaeManager.Domain.Subcontratas;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class VerificacionExternaSubcontrataRepository(CaeManagerDbContext dbContext) : IVerificacionExternaSubcontrataRepository
{
    public Task<VerificacionExternaSubcontrata?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.VerificacionesExternaSubcontrata.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);

    public void Agregar(VerificacionExternaSubcontrata verificacion) =>
        dbContext.VerificacionesExternaSubcontrata.Add(verificacion);
}
