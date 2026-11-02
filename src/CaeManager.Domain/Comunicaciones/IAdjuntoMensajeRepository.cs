namespace CaeManager.Domain.Comunicaciones;

public interface IAdjuntoMensajeRepository
{
    Task<AdjuntoMensaje?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);
}
