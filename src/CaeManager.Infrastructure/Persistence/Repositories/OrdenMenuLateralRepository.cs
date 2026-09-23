using CaeManager.Domain.Plataforma;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class OrdenMenuLateralRepository(CaeManagerDbContext dbContext) : IOrdenMenuLateralRepository
{
    public Task<OrdenMenuLateral?> ObtenerAsync(CancellationToken cancellationToken = default) =>
        dbContext.OrdenMenuLateral.SingleOrDefaultAsync(o => o.Id == OrdenMenuLateral.ClaveCanonica, cancellationToken);

    public Task<OrdenMenuLateral?> ObtenerSinSeguimientoAsync(CancellationToken cancellationToken = default) =>
        dbContext.OrdenMenuLateral.AsNoTracking()
            .SingleOrDefaultAsync(o => o.Id == OrdenMenuLateral.ClaveCanonica, cancellationToken);

    public async Task<bool> AgregarYGuardarAsync(OrdenMenuLateral orden, CancellationToken cancellationToken = default)
    {
        dbContext.OrdenMenuLateral.Add(orden);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        // Solo la clave primaria de la fila única, no cualquier 23505: otra violación de unicidad
        // sería un error real y tiene que propagarse (mismo criterio que
        // ExpiracionAsignacionesHostedService.GuardarODejarComoEstabaAsync).
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_OrdenMenuLateral"
        })
        {
            dbContext.Entry(orden).State = EntityState.Detached;
            return false;
        }
    }
}
