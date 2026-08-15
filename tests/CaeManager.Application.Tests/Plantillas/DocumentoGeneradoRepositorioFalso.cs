using CaeManager.Domain.Plantillas;

namespace CaeManager.Application.Tests.Plantillas;

public class DocumentoGeneradoRepositorioFalso : IDocumentoGeneradoRepository
{
    public List<DocumentoGenerado> Lista { get; } = [];

    public void Agregar(DocumentoGenerado documentoGenerado) => Lista.Add(documentoGenerado);

    public Task<DocumentoGenerado?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Lista.FirstOrDefault(d => d.Id == id));
}
