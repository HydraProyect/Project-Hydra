using CaeManager.Domain.Importacion;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class OperacionImportacionRepository(CaeManagerDbContext dbContext) : IOperacionImportacionRepository
{
    private const string IndiceUnicoOperacion = "IX_OperacionesImportacion_TenantId_OperacionId";

    public Task<bool> ExisteAsync(Guid operacionId, CancellationToken cancellationToken = default) =>
        dbContext.OperacionesImportacion.AnyAsync(o => o.OperacionId == operacionId, cancellationToken);

    public void Agregar(OperacionImportacion operacion) => dbContext.OperacionesImportacion.Add(operacion);

    public async Task<bool> GuardarSiOperacionNuevaAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        // Comprobar el nombre de la restricción (mismo patrón que
        // ExpiracionAsignacionesHostedService.GuardarODejarComoEstabaAsync) y no
        // cualquier 23505: una violación de unicidad distinta (un CIF repetido,
        // un DNI repetido) es un error real del plan, no la carrera de
        // idempotencia, y debe propagarse tal cual en vez de disfrazarse de
        // "ya ejecutada".
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: "23505"
        } pg && pg.ConstraintName == IndiceUnicoOperacion)
        {
            // Un SaveChangesAsync fallido NO revierte el estado Added de las
            // entidades en memoria — solo la transacción de la base de datos. Sin
            // este Clear(), el resto del plan (Empresa/Trabajador/Documento/
            // Asignación de la confirmación perdedora) se quedaría marcado para
            // guardar en el siguiente SaveChangesAsync del mismo contexto
            // compartido, aunque nada de eso llegó a persistirse aquí.
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }

    public void DescartarPendientes() => dbContext.ChangeTracker.Clear();
}
