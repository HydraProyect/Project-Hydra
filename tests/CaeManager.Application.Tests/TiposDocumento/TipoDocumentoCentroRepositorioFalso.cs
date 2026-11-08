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

    /// <summary>Sin contexto que rastree, lo confirmado es la propia lista. Ver <c>RequisitosRastreadosComoEf</c>.</summary>
    public Task<TipoDocumentoCentro?> ObtenerPorParActualizadoAsync(Guid tipoDocumentoId, Guid centroId, CancellationToken cancellationToken = default) =>
        ObtenerPorParAsync(tipoDocumentoId, centroId, cancellationToken);

    public Task<IReadOnlyList<TipoDocumentoCentro>> ObtenerPorTipoDocumentoActualizadoAsync(Guid tipoDocumentoId, CancellationToken cancellationToken = default) =>
        ObtenerPorTipoDocumentoAsync(tipoDocumentoId, cancellationToken);

    public void Agregar(TipoDocumentoCentro tipoDocumentoCentro) => Relaciones.Add(tipoDocumentoCentro);

    public void Eliminar(TipoDocumentoCentro tipoDocumentoCentro) => Relaciones.Remove(tipoDocumentoCentro);
}
