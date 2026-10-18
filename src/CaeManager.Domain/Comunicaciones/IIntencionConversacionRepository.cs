namespace CaeManager.Domain.Comunicaciones;

public interface IIntencionConversacionRepository
{
    void Agregar(IntencionConversacion intencion);
    Task<IntencionConversacion?> ObtenerPorConversacionIdAsync(Guid conversacionId, CancellationToken cancellationToken = default);
}
