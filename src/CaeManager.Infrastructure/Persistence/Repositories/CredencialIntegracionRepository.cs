using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class CredencialIntegracionRepository(CaeManagerDbContext dbContext) : ICredencialIntegracionRepository
{
    public Task<CredencialIntegracion?> ObtenerPorConexionAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default) =>
        dbContext.CredencialesIntegracion.FirstOrDefaultAsync(c => c.ConexionIntegracionId == conexionIntegracionId, cancellationToken);

    public void Agregar(CredencialIntegracion credencial) => dbContext.CredencialesIntegracion.Add(credencial);
}
