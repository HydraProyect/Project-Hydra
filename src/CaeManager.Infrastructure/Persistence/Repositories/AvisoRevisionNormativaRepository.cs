using CaeManager.Domain.VigilanciaNormativa;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class AvisoRevisionNormativaRepository(CaeManagerDbContext dbContext) : IAvisoRevisionNormativaRepository
{
    public Task<bool> ExisteParaPublicacionAsync(string identificadorBoe, CancellationToken cancellationToken = default) =>
        dbContext.AvisosRevisionNormativa.AnyAsync(a => a.IdentificadorBoe == identificadorBoe, cancellationToken);

    public Task<AvisoRevisionNormativa?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.AvisosRevisionNormativa.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public void Agregar(AvisoRevisionNormativa aviso) => dbContext.AvisosRevisionNormativa.Add(aviso);
}
