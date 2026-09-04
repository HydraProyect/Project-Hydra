using CaeManager.Domain.Auditoria;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class RegistroAccesoDocumentoSensibleRepository(
    CaeManagerDbContext dbContext, ILogger<RegistroAccesoDocumentoSensibleRepository> logger)
    : IRegistroAccesoDocumentoSensibleRepository
{
    public async Task<bool> GuardarAsync(RegistroAccesoDocumentoSensible registro, CancellationToken cancellationToken = default)
    {
        dbContext.RegistrosAccesoDocumentoSensible.Add(registro);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "42501" })
        {
            // Sesión privilegiada de plataforma (ADR-011 § 4bis, ver
            // TenantRlsConnectionInterceptor): la conexión adopta
            // cae_app_soporte, deliberadamente sin privilegio de escritura
            // sobre NINGUNA tabla — "la inspección de soporte es de solo
            // lectura, sin excepción implícita" (AutorizacionEscrituraBehavior).
            // No se abre una excepción para este registro: hacerlo sería
            // exactamente la puerta entreabierta que ese rol existe para
            // impedir. La consecuencia es un hueco real y conocido — un
            // acceso break-glass a un documento sensible hoy no puede
            // registrarse aquí — devuelto como decisión pendiente en el
            // RETURN PACKAGE de HO-099-01 en vez de resuelto en silencio.
            //
            // Un SaveChangesAsync fallido no revierte el estado Added en
            // memoria (mismo aviso que OperacionImportacionRepository) — sin
            // este Clear(), el registro se reintentaría en el próximo
            // SaveChanges de este mismo DbContext, cuando ya sabemos que
            // fallará otra vez por el mismo motivo.
            dbContext.ChangeTracker.Clear();

            logger.LogWarning(ex,
                "No se pudo registrar el acceso a {RecursoId}: la sesión no tiene privilegio de escritura (cae_app_soporte). " +
                "Acceso break-glass sin rastro en RegistroAccesoDocumentoSensible — hueco conocido, ver HO-099-01.",
                registro.DocumentoId);

            return false;
        }
    }
}
