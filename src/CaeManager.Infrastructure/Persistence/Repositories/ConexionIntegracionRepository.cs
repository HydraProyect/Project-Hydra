using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ConexionIntegracionRepository(CaeManagerDbContext dbContext) : IConexionIntegracionRepository
{
    public Task<ConexionIntegracion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ConexionesIntegracion.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public void Agregar(ConexionIntegracion conexion) => dbContext.ConexionesIntegracion.Add(conexion);
}
