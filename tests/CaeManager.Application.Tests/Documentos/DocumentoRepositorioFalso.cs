using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Tests.Documentos;

public class DocumentoRepositorioFalso : IDocumentoRepository
{
    public List<Documento> Documentos { get; } = [];

    public Task<Documento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Documentos.FirstOrDefault(d => d.Id == id));

    public void Agregar(Documento documento) => Documentos.Add(documento);
}
