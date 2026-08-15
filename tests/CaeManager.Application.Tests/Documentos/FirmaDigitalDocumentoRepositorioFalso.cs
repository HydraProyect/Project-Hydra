using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Tests.Documentos;

public class FirmaDigitalDocumentoRepositorioFalso : IFirmaDigitalDocumentoRepository
{
    public List<FirmaDigitalDocumento> Firmas { get; } = [];

    public void Agregar(FirmaDigitalDocumento firma) => Firmas.Add(firma);

    public Task<IReadOnlyList<FirmaDigitalDocumento>> ObtenerPorDocumentoAsync(
        Guid documentoId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FirmaDigitalDocumento>>(
            Firmas.Where(f => f.DocumentoId == documentoId).ToList());

    public void EliminarDeDocumento(IReadOnlyList<FirmaDigitalDocumento> firmas)
    {
        foreach (var firma in firmas) Firmas.Remove(firma);
    }
}
