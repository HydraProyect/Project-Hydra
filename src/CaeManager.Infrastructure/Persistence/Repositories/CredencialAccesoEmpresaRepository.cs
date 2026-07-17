using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class CredencialAccesoEmpresaRepository(CaeManagerDbContext dbContext) : ICredencialAccesoEmpresaRepository
{
    public Task<CredencialAccesoEmpresa?> ObtenerPorEmpresaAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
        dbContext.CredencialesAccesoEmpresa.FirstOrDefaultAsync(c => c.EmpresaId == empresaId, cancellationToken);

    public void Agregar(CredencialAccesoEmpresa credencial) => dbContext.CredencialesAccesoEmpresa.Add(credencial);
}
