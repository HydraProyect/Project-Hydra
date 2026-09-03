using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Tests.Documentos;

public class DocumentoRepositorioFalso : IDocumentoRepository
{
    public List<Documento> Documentos { get; } = [];

    /// <summary>Si se establece, la próxima llamada a <see cref="Agregar"/> la lanza en vez de agregar — para simular un fallo inesperado a mitad de construir un plan.</summary>
    public Exception? ExcepcionAlAgregar { get; set; }

    public Task<Documento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Documentos.FirstOrDefault(d => d.Id == id));

    public void Agregar(Documento documento)
    {
        if (ExcepcionAlAgregar is { } excepcion)
            throw excepcion;

        Documentos.Add(documento);
    }
}
