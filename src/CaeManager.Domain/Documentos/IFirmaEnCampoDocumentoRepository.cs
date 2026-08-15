namespace CaeManager.Domain.Documentos;

public interface IFirmaEnCampoDocumentoRepository
{
    void Agregar(FirmaEnCampoDocumento firma);

    Task<IReadOnlyList<FirmaEnCampoDocumento>> ObtenerPorDocumentoAsync(
        Guid documentoId, CancellationToken cancellationToken = default);
}
