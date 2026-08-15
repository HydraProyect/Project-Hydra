using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class SelloEmpresaRepository(CaeManagerDbContext dbContext) : ISelloEmpresaRepository
{
    public void Agregar(SelloEmpresa sello) => dbContext.SellosEmpresa.Add(sello);

    public async Task<SelloEmpresa?> ObtenerPorEmpresaAsync(
        Guid empresaId, CancellationToken cancellationToken = default) =>
        await dbContext.SellosEmpresa.FirstOrDefaultAsync(s => s.EmpresaId == empresaId, cancellationToken);
}
