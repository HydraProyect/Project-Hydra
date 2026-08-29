using CaeManager.Domain.BusquedaGlobal;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class EventoRecienteUsuarioRepository(CaeManagerDbContext dbContext) : IEventoRecienteUsuarioRepository
{
    public void Agregar(EventoRecienteUsuario evento) => dbContext.EventosRecientesUsuario.Add(evento);

    public async Task PurgarExcedentesAsync(Guid usuarioId, int maximoAConservar, CancellationToken cancellationToken)
    {
        var excedentes = await dbContext.EventosRecientesUsuario
            .Where(e => e.UsuarioId == usuarioId)
            .OrderByDescending(e => e.OcurridoEnUtc)
            .Skip(maximoAConservar)
            .ToListAsync(cancellationToken);

        if (excedentes.Count == 0) return;

        dbContext.EventosRecientesUsuario.RemoveRange(excedentes);
    }
}
