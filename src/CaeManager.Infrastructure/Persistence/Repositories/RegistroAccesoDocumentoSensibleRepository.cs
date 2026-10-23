using CaeManager.Domain.Auditoria;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class RegistroAccesoDocumentoSensibleRepository(
    CaeManagerDbContext dbContext, ILogger<RegistroAccesoDocumentoSensibleRepository> logger)
    : IRegistroAccesoDocumentoSensibleRepository
{
    private const string Registrado = "registrado";

    public async Task<bool> GuardarAsync(RegistroAccesoDocumentoSensible registro, CancellationToken cancellationToken = default)
    {
        // Sesión Privilegiada (ADR-011 § 4bis, ver TenantRlsConnectionInterceptor): la
        // conexión adopta cae_app_soporte, que solo tiene SELECT sobre esta tabla y no lo
        // pierde — "la inspección de soporte es de solo lectura, sin excepción implícita"
        // (AutorizacionEscrituraBehavior). El acceso de Soporte TALVEG a un documento sensible
        // se registra igual (DEC-36, REC-208) por la única puerta de escritura de esa sesión:
        // la función app_registrar_acceso_documento_sensible_por_soporte, que vuelve a
        // comprobar la sesión contra el contexto RLS firmado y pone ella misma Tenant, actor y
        // vía. ViaAcceso = SesionPrivilegiada sale de la misma selección del token que decide
        // el rol de la conexión (ActorAuditoriaDesdeSesion), así que las dos ramas no divergen.
        if (registro.EsPrivilegiado)
            return await GuardarPorSesionPrivilegiadaAsync(registro, cancellationToken);

        dbContext.RegistrosAccesoDocumentoSensible.Add(registro);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.InsufficientPrivilege })
        {
            // Una conexión sin INSERT que no llegó como Sesión Privilegiada: no debería
            // ocurrir. Se devuelve false para que el servicio falle en cerrado y no sirva el
            // documento sin rastro; nunca se tolera en silencio.
            //
            // Un SaveChangesAsync fallido no revierte el estado Added en memoria (mismo aviso
            // que OperacionImportacionRepository): sin este Clear(), el registro se
            // reintentaría en el próximo SaveChanges de este mismo DbContext.
            dbContext.ChangeTracker.Clear();

            logger.LogError(ex,
                "No se pudo registrar el acceso a {RecursoId}: la conexión no tiene privilegio de escritura. " +
                "El documento no se sirve.",
                registro.DocumentoId);

            return false;
        }
    }

    private async Task<bool> GuardarPorSesionPrivilegiadaAsync(
        RegistroAccesoDocumentoSensible registro, CancellationToken cancellationToken)
    {
        if (registro.ViaAccesoId is not { } sesionId)
        {
            logger.LogError(
                "No se pudo registrar el acceso a {RecursoId}: la vía es SesionPrivilegiada pero no nombra la sesión. " +
                "El documento no se sirve.",
                registro.DocumentoId);
            return false;
        }

        string codigo;
        try
        {
            // Los cinco valores van parametrizados por EF.
            codigo = await dbContext.Database
                .SqlQuery<string>(
                    $"SELECT app_registrar_acceso_documento_sensible_por_soporte({sesionId}, {registro.Id}, {registro.DocumentoId}, {registro.Sensibilidad.ToString()}, {registro.TipoAcceso.ToString()}) AS \"Value\"")
                .SingleAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            // Contexto RLS firmado inválido o conexión sin el rol de soporte (sin EXECUTE).
            logger.LogError(ex,
                "No se pudo registrar el acceso a {RecursoId} en la Sesión Privilegiada {SesionId}. El documento no se sirve.",
                registro.DocumentoId, sesionId);
            return false;
        }

        if (codigo == Registrado)
            return true;

        logger.LogError(
            "La base rechazó el registro del acceso a {RecursoId} en la Sesión Privilegiada {SesionId} ({Codigo}). " +
            "El documento no se sirve.",
            registro.DocumentoId, sesionId, codigo);
        return false;
    }
}
