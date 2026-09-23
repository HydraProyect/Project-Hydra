using CaeManager.Domain.Auditoria;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.Repositories;

/// <inheritdoc cref="IRegistroAccesoDatoSensibleRepository"/>
public class RegistroAccesoDatoSensibleRepository(
    CaeManagerDbContext dbContext, ILogger<RegistroAccesoDatoSensibleRepository> logger)
    : IRegistroAccesoDatoSensibleRepository
{
    public async Task GuardarAsync(RegistroAuditoria registro, CancellationToken cancellationToken = default)
    {
        // El registro se guarda desde un camino de LECTURA con el DbContext con
        // ámbito, el mismo de cualquier otro handler del circuito. Un
        // SaveChanges aquí volcaría también lo que otro haya dejado pendiente,
        // sin que ese otro lo haya decidido. Las Queries de credenciales
        // proyectan sin rastrear, así que en uso normal no hay nada pendiente;
        // si lo hay, se falla cerrado —ni se guarda nada ajeno ni se entrega el
        // dato— en vez de volcarlo en silencio.
        if (dbContext.ChangeTracker.Entries().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException(
                "No se registra el acceso a un dato sensible con cambios pendientes ajenos en el contexto: " +
                "el guardado los persistiría sin que su dueño lo haya decidido.");

        dbContext.RegistrosAuditoria.Add(registro);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "42501" })
        {
            // Sesión privilegiada de plataforma: la conexión adopta
            // cae_app_soporte, sin privilegio de escritura sobre ninguna tabla
            // (mismo caso y mismo criterio que RegistroAccesoDocumentoSensibleRepository).
            // Solo llega aquí la precarga sin contraseña
            // (IConsultaDeDatosDeCredencial): las consultas de secretos se
            // deniegan antes, en AutorizacionSecretosDeTenantBehavior. Hueco
            // conocido: esa lectura del usuario queda sin rastro en la auditoría.
            dbContext.Entry(registro).State = EntityState.Detached;

            logger.LogWarning(ex,
                "No se pudo registrar el acceso a dato sensible {EntidadTipo} {EntidadId}: la sesión no tiene " +
                "privilegio de escritura (cae_app_soporte).",
                registro.EntidadTipo, registro.EntidadId);
        }
        catch
        {
            // Que un fallo no deje el registro Added para el próximo
            // SaveChanges de otro handler.
            dbContext.Entry(registro).State = EntityState.Detached;
            throw;
        }
    }
}
