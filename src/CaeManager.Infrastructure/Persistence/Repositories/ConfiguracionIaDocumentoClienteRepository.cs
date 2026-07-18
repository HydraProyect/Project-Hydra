using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ConfiguracionIaDocumentoClienteRepository(CaeManagerDbContext dbContext) : IConfiguracionIaDocumentoClienteRepository
{
    public Task<ConfiguracionIaDocumentoCliente?> ObtenerAsync(Guid clienteId, Guid tipoDocumentoId, CancellationToken cancellationToken = default) =>
        dbContext.ConfiguracionesIaDocumentoCliente
            .FirstOrDefaultAsync(c => c.ClienteId == clienteId && c.TipoDocumentoId == tipoDocumentoId, cancellationToken);

    public async Task<IReadOnlyList<ConfiguracionIaDocumentoCliente>> ObtenerPorClienteAsync(Guid clienteId, CancellationToken cancellationToken = default) =>
        await dbContext.ConfiguracionesIaDocumentoCliente.Where(c => c.ClienteId == clienteId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> ObtenerNombresTiposDocumentoSinLecturaIaAsync(Guid clienteId, CancellationToken cancellationToken = default)
    {
        var overridesDesactivados = await dbContext.ConfiguracionesIaDocumentoCliente
            .Where(c => c.ClienteId == clienteId && !c.Activa)
            .Select(c => c.TipoDocumentoId)
            .ToListAsync(cancellationToken);

        var nombresPorGlobalDesactivado = await dbContext.TiposDocumento
            .Where(t => !t.LecturaIaActiva)
            .Select(t => new { t.Id, t.Nombre })
            .ToListAsync(cancellationToken);

        var idsSinLectura = overridesDesactivados.Concat(nombresPorGlobalDesactivado.Select(t => t.Id)).ToHashSet();
        if (idsSinLectura.Count == 0) return [];

        return await dbContext.TiposDocumento
            .Where(t => idsSinLectura.Contains(t.Id))
            .Select(t => t.Nombre)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public void Agregar(ConfiguracionIaDocumentoCliente configuracion) => dbContext.ConfiguracionesIaDocumentoCliente.Add(configuracion);

    public void Eliminar(ConfiguracionIaDocumentoCliente configuracion) => dbContext.ConfiguracionesIaDocumentoCliente.Remove(configuracion);
}
