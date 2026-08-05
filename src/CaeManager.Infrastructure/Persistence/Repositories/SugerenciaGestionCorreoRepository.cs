using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class SugerenciaGestionCorreoRepository(CaeManagerDbContext dbContext) : ISugerenciaGestionCorreoRepository
{
    public Task<SugerenciaGestionCorreo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.SugerenciasGestionCorreo.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public void Agregar(SugerenciaGestionCorreo sugerencia) => dbContext.SugerenciasGestionCorreo.Add(sugerencia);
}
