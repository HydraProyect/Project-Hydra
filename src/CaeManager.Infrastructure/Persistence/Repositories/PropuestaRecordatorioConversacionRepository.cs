using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class PropuestaRecordatorioConversacionRepository(CaeManagerDbContext dbContext)
    : IPropuestaRecordatorioConversacionRepository
{
    public Task<PropuestaRecordatorioConversacion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.PropuestasRecordatorioConversacion.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
}
