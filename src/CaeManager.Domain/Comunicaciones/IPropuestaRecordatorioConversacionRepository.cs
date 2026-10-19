namespace CaeManager.Domain.Comunicaciones;

public interface IPropuestaRecordatorioConversacionRepository
{
    Task<PropuestaRecordatorioConversacion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);
}
