using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Tests.TiposDocumento;

public class TipoDocumentoCentroRepositorioFalso : ITipoDocumentoCentroRepository
{
    public List<TipoDocumentoCentro> Relaciones { get; } = [];

    public Task<TipoDocumentoCentro?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Relaciones.FirstOrDefault(r => r.Id == id));

    public Task<IReadOnlyList<TipoDocumentoCentro>> ObtenerPorTipoDocumentoAsync(Guid tipoDocumentoId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TipoDocumentoCentro>>(Relaciones.Where(r => r.TipoDocumentoId == tipoDocumentoId).ToList());

    public Task<IReadOnlyList<TipoDocumentoCentro>> ObtenerPorCentroAsync(Guid centroId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TipoDocumentoCentro>>(Relaciones.Where(r => r.CentroId == centroId).ToList());

    public Task<TipoDocumentoCentro?> ObtenerPorParAsync(Guid tipoDocumentoId, Guid centroId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Relaciones.FirstOrDefault(r => r.TipoDocumentoId == tipoDocumentoId && r.CentroId == centroId));

    public void Agregar(TipoDocumentoCentro tipoDocumentoCentro) => Relaciones.Add(tipoDocumentoCentro);

    public void Eliminar(TipoDocumentoCentro tipoDocumentoCentro) => Relaciones.Remove(tipoDocumentoCentro);
}
