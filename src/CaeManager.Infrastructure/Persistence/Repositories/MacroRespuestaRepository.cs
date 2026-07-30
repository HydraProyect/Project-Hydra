using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class MacroRespuestaRepository(CaeManagerDbContext dbContext) : IMacroRespuestaRepository
{
    public Task<MacroRespuesta?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.MacrosRespuesta.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    public void Agregar(MacroRespuesta macro) => dbContext.MacrosRespuesta.Add(macro);
}
