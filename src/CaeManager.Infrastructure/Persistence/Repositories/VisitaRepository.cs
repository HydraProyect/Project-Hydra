using CaeManager.Domain.Visitas;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class VisitaRepository(CaeManagerDbContext dbContext) : IVisitaRepository
{
    public Task<Visita?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Visitas.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);

    public async Task<Visita?> ObtenerPorIdActualizadoAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var visita = await ObtenerPorIdAsync(id, cancellationToken);
        if (visita is not null)
        {
            // Una consulta no refresca una entidad ya rastreada: sin esto se devolvería la copia vieja.
            await dbContext.Entry(visita).ReloadAsync(cancellationToken);
        }

        return visita;
    }

    public void Agregar(Visita visita) => dbContext.Visitas.Add(visita);
}
