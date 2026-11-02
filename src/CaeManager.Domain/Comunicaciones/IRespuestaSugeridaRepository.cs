namespace CaeManager.Domain.Comunicaciones;

public interface IRespuestaSugeridaRepository
{
    Task<RespuestaSugerida?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RespuestaSugerida?> ObtenerActivaPorConversacionAsync(Guid conversacionId, CancellationToken cancellationToken = default);

    void Agregar(RespuestaSugerida respuesta);
}
