using CaeManager.Application.Common.Archivos;
using CaeManager.Domain.Archivos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

/// <inheritdoc cref="IArchivosPendientesDeReferencia" />
public class ArchivosPendientesDeReferenciaRepository(CaeManagerDbContext dbContext) : IArchivosPendientesDeReferencia
{
    public async Task ApuntarAsync(string clave, CancellationToken cancellationToken = default)
    {
        // Dentro de una transacción ya abierta la fila se desharía con ella, justo en el fallo que tiene que sobrevivir.
        if (dbContext.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Un archivo pendiente de referencia se apunta fuera de cualquier transacción.");

        var tenantId = dbContext.TenantPropietarioId
            ?? throw new InvalidOperationException("Un archivo pendiente de referencia se apunta en el Tenant propietario en curso.");

        // Valida la clave con las mismas reglas que la entidad antes de escribir nada.
        var pendiente = new ArchivoPendienteDeReferencia(clave, DateTime.UtcNow);

        // SQL crudo y no Add + SaveChangesAsync: el guardado tiene que confirmar solo esta fila. El contexto del llamador
        // (en Blazor Server, el del circuito) puede llevar cambios pendientes que confirma el alta de después, como el
        // adjunto de ActualizarDocumentoDesdeAdjuntoCommand; un SaveChangesAsync aquí los confirmaría antes de tiempo. Los
        // cuatro valores van parametrizados; el Tenant es el del contexto y la RLS lo vuelve a exigir (WITH CHECK), y la
        // hora es la de la base, la misma con la que el barrido mide las 24 h.
        await dbContext.Database.ExecuteSqlAsync(
            $"""INSERT INTO "ArchivosPendientesDeReferencia" ("Id", "Clave", "GuardadoEnUtc", "TenantId") VALUES ({pendiente.Id}, {pendiente.Clave}, now(), {tenantId})""",
            cancellationToken);
    }

    public Task QuitarAsync(string clave, CancellationToken cancellationToken = default) =>
        dbContext.ArchivosPendientesDeReferencia.Where(p => p.Clave == clave).ExecuteDeleteAsync(cancellationToken);

    public async Task<IReadOnlyList<ArchivoPendienteDeReferencia>> ObtenerApuntadosHastaAsync(
        DateTime limiteUtc, int maximo, CancellationToken cancellationToken = default) =>
        await dbContext.ArchivosPendientesDeReferencia.AsNoTracking()
            .Where(p => p.GuardadoEnUtc <= limiteUtc)
            .OrderBy(p => p.GuardadoEnUtc)
            .Take(maximo)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlySet<string>> ReferenciadasAsync(
        IReadOnlyCollection<string> claves, CancellationToken cancellationToken = default)
    {
        // Sin Tenant no se decide que nada falte: el filtro global no devolvería nada y todo parecería huérfano.
        var tenantId = dbContext.TenantPropietarioId
            ?? throw new InvalidOperationException("Las referencias se comprueban en el Tenant propietario en curso.");
        if (claves.Count == 0)
            return new HashSet<string>();

        // También los Documentos en la papelera: su archivo sigue siendo suyo hasta que la retención los purgue. Al retirar
        // el filtro combinado se repone explícitamente el Tenant; RLS sigue aplicándose.
        var deDocumentos = await dbContext.Documentos.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.ArchivoUrl != null && claves.Contains(d.ArchivoUrl))
            .Select(d => d.ArchivoUrl!)
            .ToListAsync(cancellationToken);
        var deVersiones = await dbContext.PlantillasDocumentoVersion.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.TenantId == tenantId && v.ArchivoOriginalUrl != null && claves.Contains(v.ArchivoOriginalUrl))
            .Select(v => v.ArchivoOriginalUrl!)
            .ToListAsync(cancellationToken);

        return deDocumentos.Concat(deVersiones).ToHashSet(StringComparer.Ordinal);
    }

    public Task DescartarAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ArchivosPendientesDeReferencia.Where(p => p.Id == id).ExecuteDeleteAsync(cancellationToken);
}
