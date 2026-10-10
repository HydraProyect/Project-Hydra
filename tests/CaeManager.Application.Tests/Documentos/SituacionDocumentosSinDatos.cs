using CaeManager.Application.Documentos.SituacionEnCentro;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Para los tests que no miran la segunda línea del documento: ninguna acreditación y ninguna reclamación.
/// </summary>
public sealed class SituacionDocumentosSinDatos : ISituacionDocumentosEnCentrosService
{
    public Task<SituacionDocumentosEnCentros> CargarAsync(
        IReadOnlyCollection<Guid> centroIds,
        IReadOnlyCollection<Guid> documentoIds,
        IReadOnlyCollection<Guid> trabajadorIdsConAusentes,
        IReadOnlyCollection<Guid> tipoDocumentoIdsDeAusentes,
        CancellationToken cancellationToken) =>
        Task.FromResult(SituacionDocumentosEnCentros.Vacia);
}
