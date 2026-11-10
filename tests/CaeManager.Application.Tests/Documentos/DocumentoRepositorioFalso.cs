using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Tests.Documentos;

public class DocumentoRepositorioFalso : IDocumentoRepository
{
    public List<Documento> Documentos { get; } = [];

    /// <summary>Si se establece, la próxima llamada a <see cref="Agregar"/> la lanza en vez de agregar — para simular un fallo inesperado a mitad de construir un plan.</summary>
    public Exception? ExcepcionAlAgregar { get; set; }

    public Task<Documento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Documentos.FirstOrDefault(d => d.Id == id));

    /// <summary>
    /// Lo que otra operación confirma en la base mientras se espera el candado. Solo lo ve la lectura «…Actualizado»,
    /// igual que EF, que no repone una instancia ya rastreada salvo que se relea.
    /// </summary>
    public Action<Documento>? CambioEnLaBase { get; set; }

    public Task<Documento?> ObtenerIncluidoEliminadoActualizadoAsync(Guid id, Guid? tenantId, CancellationToken cancellationToken = default)
    {
        var documento = Documentos.FirstOrDefault(d => d.Id == id && d.TenantId == tenantId);
        if (documento is not null)
            CambioEnLaBase?.Invoke(documento);
        return Task.FromResult(documento);
    }

    public void Agregar(Documento documento)
    {
        if (ExcepcionAlAgregar is { } excepcion)
            throw excepcion;

        Documentos.Add(documento);
    }
}
