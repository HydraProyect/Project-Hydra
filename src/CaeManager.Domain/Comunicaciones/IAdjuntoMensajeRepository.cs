namespace CaeManager.Domain.Comunicaciones;

public interface IAdjuntoMensajeRepository
{
    Task<AdjuntoMensaje?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Retira del contexto un adjunto cuyo cambio no se pudo guardar.</summary>
    void OlvidarCambios(AdjuntoMensaje adjunto);
}
