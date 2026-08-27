namespace CaeManager.Domain.Blindaje42;

public interface ISolicitudCertificacionTgssRepository
{
    Task<SolicitudCertificacionTgss?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(SolicitudCertificacionTgss solicitud);
}
