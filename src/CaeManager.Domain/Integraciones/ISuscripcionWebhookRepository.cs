namespace CaeManager.Domain.Integraciones;

public interface ISuscripcionWebhookRepository
{
    Task<SuscripcionWebhook?> ObtenerPorConexionAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default);

    /// <summary>Suscripciones a menos de <paramref name="dentroDe"/> de expirar — para el job de renovación.</summary>
    Task<IReadOnlyList<SuscripcionWebhook>> ObtenerProximasAExpirarAsync(TimeSpan dentroDe, CancellationToken cancellationToken = default);

    void Agregar(SuscripcionWebhook suscripcion);
}
