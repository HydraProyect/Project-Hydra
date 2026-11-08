using CaeManager.Domain.Gestiones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class GestionAutomaticaRepository(CaeManagerDbContext dbContext) : IGestionAutomaticaRepository
{
    public Task<GestionAutomatica?> ObtenerPorDocumentoIdAsync(Guid documentoId, CancellationToken cancellationToken = default) =>
        dbContext.GestionesAutomaticas
            .Include(g => g.Pasos)
            .FirstOrDefaultAsync(g => g.DocumentoId == documentoId, cancellationToken);

    public void Agregar(GestionAutomatica gestion) => dbContext.GestionesAutomaticas.Add(gestion);
}
