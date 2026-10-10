using CaeManager.Domain.Configuracion;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class OrdenCajasFichaRepository(CaeManagerDbContext dbContext) : IOrdenCajasFichaRepository
{
    public Task<OrdenCajasFicha?> ObtenerAsync(Guid usuarioId, string tipoFicha, CancellationToken cancellationToken = default) =>
        dbContext.OrdenesCajasFicha.FirstOrDefaultAsync(
            o => o.UsuarioId == usuarioId && o.TipoFicha == tipoFicha, cancellationToken);

    public void Agregar(OrdenCajasFicha orden) => dbContext.OrdenesCajasFicha.Add(orden);

    public void Eliminar(OrdenCajasFicha orden) => dbContext.OrdenesCajasFicha.Remove(orden);
}
