using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class EmpresaClienteRepository(CaeManagerDbContext dbContext) : IEmpresaClienteRepository
{
    public async Task<IReadOnlyList<EmpresaCliente>> ObtenerPorEmpresaAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
        await dbContext.Set<EmpresaCliente>().Where(ec => ec.EmpresaId == empresaId).ToListAsync(cancellationToken);

    public void Agregar(EmpresaCliente empresaCliente) => dbContext.Set<EmpresaCliente>().Add(empresaCliente);

    public void Eliminar(EmpresaCliente empresaCliente) => dbContext.Set<EmpresaCliente>().Remove(empresaCliente);
}
