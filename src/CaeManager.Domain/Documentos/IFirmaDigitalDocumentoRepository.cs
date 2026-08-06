namespace CaeManager.Domain.Documentos;

public interface IFirmaDigitalDocumentoRepository
{
    void Agregar(FirmaDigitalDocumento firma);

    Task<IReadOnlyList<FirmaDigitalDocumento>> ObtenerPorDocumentoAsync(
        Guid documentoId, CancellationToken cancellationToken = default);

    /// <summary>Al renovar el archivo, el resultado anterior deja de describir nada: se borra y se recrea con el vigente.</summary>
    void EliminarDeDocumento(IReadOnlyList<FirmaDigitalDocumento> firmas);
}
