using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class SugerenciaVisitaCorreoRepository(CaeManagerDbContext dbContext) : ISugerenciaVisitaCorreoRepository
{
    public Task<SugerenciaVisitaCorreo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.SugerenciasVisitaCorreo.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public void Agregar(SugerenciaVisitaCorreo sugerencia) => dbContext.SugerenciasVisitaCorreo.Add(sugerencia);
}
