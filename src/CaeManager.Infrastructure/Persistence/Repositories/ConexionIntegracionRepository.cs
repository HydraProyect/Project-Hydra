using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ConexionIntegracionRepository(CaeManagerDbContext dbContext) : IConexionIntegracionRepository
{
    public Task<ConexionIntegracion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ConexionesIntegracion.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<ConexionIntegracion?> ObtenerActualPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ObtenerActualAsync(dbContext.ConexionesIntegracion.Where(c => c.Id == id), cancellationToken);

    public async Task<bool> SigueOperativaVerificadaAsync(Guid id, DateTime ahoraUtc, CancellationToken cancellationToken = default)
    {
        // Proyección sin rastreo: los valores salen de la base, no de la instancia que ya tenga el contexto.
        var estado = await dbContext.ConexionesIntegracion.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { c.Estado, c.Proveedor, c.IdentidadVerificadaUtc })
            .FirstOrDefaultAsync(cancellationToken);

        return estado is not null
            && ConexionIntegracion.EsOperativaVerificada(estado.Estado, estado.Proveedor, estado.IdentidadVerificadaUtc, ahoraUtc);
    }

    public void Agregar(ConexionIntegracion conexion) => dbContext.ConexionesIntegracion.Add(conexion);
}
