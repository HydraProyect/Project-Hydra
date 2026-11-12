using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class DocumentoRepository(CaeManagerDbContext dbContext) : IDocumentoRepository
{
    public Task<Documento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Documentos.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    // Al retirar el filtro combinado (Tenant y eliminados) se repone a mano el Tenant propietario. RLS sigue aplicándose.
    public async Task<bool> PodriaExistirAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.TenantPropietarioId is not { } tenantId
        || await dbContext.Documentos.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(d => d.Id == id && d.TenantId == tenantId, cancellationToken);

    // Al retirar el filtro combinado se repone explícitamente el Tenant; RLS sigue aplicándose.
    public Task<Documento?> ObtenerIncluidoEliminadoActualizadoAsync(Guid id, Guid? tenantId, CancellationToken cancellationToken = default) =>
        dbContext.ObtenerActualAsync(
            dbContext.Documentos.IgnoreQueryFilters().Where(d => d.Id == id && d.TenantId == tenantId), cancellationToken);

    // Sin rastrear, para que una instancia que el contexto (el del circuito vive toda la visita) ya rastree con cambios
    // pendientes no tape la fila de la base: ObtenerActualAsync no relee esas para no perder los cambios. Mismo filtro
    // repuesto a mano; RLS sigue aplicándose.
    public Task<Documento?> ObtenerIncluidoEliminadoSinRastrearAsync(Guid id, Guid? tenantId, CancellationToken cancellationToken = default) =>
        dbContext.Documentos.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, cancellationToken);

    // SQL crudo por necesidad, como TransaccionDocumentoBloqueado: EF no expresa FOR SHARE, y la sentencia va sin componer
    // para que el bloqueo quede en la exterior. Repone el Tenant a mano y no filtra eliminados (la relectura los devuelve
    // y decide quien llama); bajo cae_app_runtime RLS sigue aplicando, también la política de UPDATE que FOR SHARE exige.
    // FOR SHARE y no FOR UPDATE: basta con que nadie cambie el Documento hasta confirmar, sin excluir otros lectores que
    // también lo bloqueen para lo mismo.
    public async Task<Documento?> BloquearYObtenerIncluidoEliminadoSinRastrearAsync(
        Guid id, Guid? tenantId, CancellationToken cancellationToken = default)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "El bloqueo del Documento necesita una transacción abierta: fuera de ella se soltaría en la misma sentencia.");
        if (tenantId is not { } tenant)
            return null;

        await dbContext.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM "Documentos"
            WHERE "Id" = {id} AND "TenantId" = {tenant}
            FOR SHARE
            """).ToListAsync(cancellationToken);
        return await ObtenerIncluidoEliminadoSinRastrearAsync(id, tenant, cancellationToken);
    }

    public void Agregar(Documento documento) => dbContext.Documentos.Add(documento);
}
