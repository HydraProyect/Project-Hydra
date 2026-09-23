using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ReclamacionBuzonIntegracionRepository(CaeManagerDbContext dbContext) : IReclamacionBuzonIntegracionRepository
{
    public const string IndiceUnicoBuzonEmail = "IX_ReclamacionesBuzonIntegracion_BuzonEmail";

    public void Reclamar(ReclamacionBuzonIntegracion reclamacion) => dbContext.ReclamacionesBuzonIntegracion.Add(reclamacion);

    public async Task<bool> GuardarCambiosSiBuzonLibreAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        // Comprobar el nombre de la restricción (mismo patrón que
        // OperacionImportacionRepository.GuardarSiOperacionNuevaAsync) y no
        // cualquier 23505: una violación de unicidad distinta en el mismo
        // SaveChangesAsync (p. ej. el nombre de la conexión) es un error real
        // del plan, no la guarda de buzón, y debe propagarse tal cual.
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: "23505"
        } pg && pg.ConstraintName == IndiceUnicoBuzonEmail)
        {
            // Un SaveChangesAsync fallido no revierte el estado Added de las
            // entidades en memoria — solo la transacción de la base de
            // datos. Sin este Clear(), la ConexionIntegracion/CredencialIntegracion/
            // SuscripcionWebhook del mismo Unit of Work compartido quedarían
            // marcadas para guardar en el siguiente SaveChangesAsync, aunque
            // nada de eso llegó a persistirse aquí.
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task LiberarAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default)
    {
        var reclamacion = await dbContext.ReclamacionesBuzonIntegracion
            .FirstOrDefaultAsync(r => r.ConexionIntegracionId == conexionIntegracionId, cancellationToken);
        if (reclamacion is not null)
            dbContext.ReclamacionesBuzonIntegracion.Remove(reclamacion);
    }
}
