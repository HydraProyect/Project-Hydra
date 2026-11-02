using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class AdjuntoMensajeRepository(CaeManagerDbContext dbContext) : IAdjuntoMensajeRepository
{
    public Task<AdjuntoMensaje?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.AdjuntosMensaje.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public void OlvidarCambios(AdjuntoMensaje adjunto) => dbContext.Entry(adjunto).State = EntityState.Detached;
}
