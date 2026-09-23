using CaeManager.Domain.Plataforma;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class OrdenMenuLateralRepository(CaeManagerDbContext dbContext) : IOrdenMenuLateralRepository
{
    public Task<OrdenMenuLateral?> ObtenerAsync(CancellationToken cancellationToken = default) =>
        dbContext.OrdenMenuLateral.SingleOrDefaultAsync(o => o.Id == OrdenMenuLateral.ClaveCanonica, cancellationToken);

    public void Agregar(OrdenMenuLateral orden) => dbContext.OrdenMenuLateral.Add(orden);
}
