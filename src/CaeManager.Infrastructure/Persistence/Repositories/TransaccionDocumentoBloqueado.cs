using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Verificacion;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

/// <summary>
/// Ver <see cref="ITransaccionDocumentoBloqueado"/>. Mismo envoltorio que
/// <see cref="TrabajoAnalisisDocumentoRepository.ReclamarSiguientePendienteAsync"/>:
/// la transacción explícita va dentro de la estrategia de ejecución del
/// contexto, porque con EnableRetryOnFailure activo una transacción abierta
/// por el propio código fuera de ella lanza InvalidOperationException.
///
/// SQL crudo por necesidad: EF no expresa <c>FOR UPDATE</c>. Salta el filtro
/// global de EF, así que repite a mano sus dos condiciones —Tenant
/// propietario en curso y no eliminado—; bajo cae_app_runtime la política RLS
/// de la tabla sigue aplicando. Los valores van parametrizados por EF.
/// </summary>
public class TransaccionDocumentoBloqueado(CaeManagerDbContext dbContext, ITenantActual tenantActual)
    : ITransaccionDocumentoBloqueado
{
    public Task<T> EjecutarAsync<T>(
        Guid documentoId, Func<Guid?, CancellationToken, Task<T>> operacion, CancellationToken cancellationToken = default)
    {
        var estrategia = dbContext.Database.CreateExecutionStrategy();

        return estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            Guid? versionActual = null;
            if (tenantActual.TenantId is { } tenantId)
            {
                // Sin componer la consulta (nada de FirstOrDefault): EF la
                // envolvería en una subconsulta, y el bloqueo debe quedar en
                // la sentencia exterior tal cual.
                var versiones = await dbContext.Database.SqlQuery<Guid>($"""
                    SELECT "Version" AS "Value" FROM "Documentos"
                    WHERE "Id" = {documentoId} AND "TenantId" = {tenantId} AND NOT "EstaEliminado"
                    FOR UPDATE
                    """).ToListAsync(cancellationToken);
                versionActual = versiones.Count == 1 ? versiones[0] : null;

                // Resolver una revisión no toca el Documento, pero sí su fila
                // de RevisionIaDocumento: bloquearlas también serializa esa
                // decisión manual con la operación.
                await dbContext.Database.SqlQuery<int>($"""
                    SELECT 1 AS "Value" FROM "RevisionesIaDocumento"
                    WHERE "DocumentoId" = {documentoId} AND "TenantId" = {tenantId}
                    FOR UPDATE
                    """).ToListAsync(cancellationToken);
            }

            var resultado = await operacion(versionActual, cancellationToken);
            await transaccion.CommitAsync(cancellationToken);
            return resultado;
        });
    }
}
