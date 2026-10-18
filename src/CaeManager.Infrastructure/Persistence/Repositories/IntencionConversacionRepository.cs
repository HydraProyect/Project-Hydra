using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class IntencionConversacionRepository(CaeManagerDbContext dbContext) : IIntencionConversacionRepository
{
    public void Agregar(IntencionConversacion intencion) => dbContext.IntencionesConversacion.Add(intencion);
    public Task<IntencionConversacion?> ObtenerPorConversacionIdAsync(Guid conversacionId, CancellationToken cancellationToken = default) =>
        dbContext.IntencionesConversacion.FirstOrDefaultAsync(i => i.ConversacionId == conversacionId, cancellationToken);
}
