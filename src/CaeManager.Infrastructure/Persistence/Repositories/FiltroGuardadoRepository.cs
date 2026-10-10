using CaeManager.Domain.Configuracion;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class FiltroGuardadoRepository(CaeManagerDbContext dbContext) : IFiltroGuardadoRepository
{
    public Task<FiltroGuardado?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.FiltrosGuardados.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

    public Task<bool> ExisteConNombreAsync(Guid usuarioId, string pantalla, string nombre, CancellationToken cancellationToken = default) =>
        dbContext.FiltrosGuardados.AnyAsync(
            f => f.UsuarioId == usuarioId && f.Pantalla == pantalla && f.Nombre == nombre, cancellationToken);

    public async Task<FiltroGuardado?> ObtenerVistaRecordadaAsync(Guid usuarioId, string pantalla, CancellationToken cancellationToken = default)
    {
        var vista = await dbContext.FiltrosGuardados.FirstOrDefaultAsync(
            f => f.UsuarioId == usuarioId && f.Pantalla == pantalla && f.Nombre == FiltroGuardado.NombreVistaRecordada,
            cancellationToken);
        if (vista is null) return null;

        // Si el contexto ya rastreaba la fila, la consulta devuelve esa instancia con los
        // valores que leyó entonces. Guardar sobre ella compararía contra una foto vieja:
        // volver a poner el valor que este circuito ya conocía no emitiría ningún UPDATE y
        // se quedaría el de la otra pestaña. Recargar la deja como está en la base.
        var entrada = dbContext.Entry(vista);
        await entrada.ReloadAsync(cancellationToken);

        // Borrada entre la consulta y la recarga: ya no hay vista.
        return entrada.State == EntityState.Detached ? null : vista;
    }

    public void Agregar(FiltroGuardado filtro) => dbContext.FiltrosGuardados.Add(filtro);

    public void Eliminar(FiltroGuardado filtro) => dbContext.FiltrosGuardados.Remove(filtro);
}
