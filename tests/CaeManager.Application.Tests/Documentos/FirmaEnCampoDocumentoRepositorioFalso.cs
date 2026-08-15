using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Tests.Documentos;

public class FirmaEnCampoDocumentoRepositorioFalso : IFirmaEnCampoDocumentoRepository
{
    public List<FirmaEnCampoDocumento> Firmas { get; } = [];

    public void Agregar(FirmaEnCampoDocumento firma) => Firmas.Add(firma);

    public Task<IReadOnlyList<FirmaEnCampoDocumento>> ObtenerPorDocumentoAsync(
        Guid documentoId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FirmaEnCampoDocumento>>(
            Firmas.Where(f => f.DocumentoId == documentoId).OrderBy(f => f.FirmadoEnUtc).ToList());
}
