namespace CaeManager.Domain.Comunicaciones;

public interface IAdjuntoMensajeRepository
{
    Task<AdjuntoMensaje?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// El Documento que la base tiene para este adjunto, sin pasar por lo que el contexto lleve en memoria. Prueba un alta:
    /// la transición se guarda con el Documento y nada vuelve a limpiar este campo.
    /// </summary>
    Task<Guid?> ObtenerDocumentoGuardadoIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Retira del contexto un adjunto cuyo cambio no se pudo guardar, con la auditoría que lo describía.</summary>
    void OlvidarCambios(AdjuntoMensaje adjunto);
}
