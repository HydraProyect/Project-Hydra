namespace CaeManager.Domain.Integraciones;

public interface ISuscripcionWebhookRepository
{
    Task<SuscripcionWebhook?> ObtenerPorConexionAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Como <see cref="ObtenerPorConexionAsync"/>, pero con los valores de la base aunque el contexto ya tuviera rastreada
    /// la fila (ver <see cref="IConexionIntegracionRepository.ObtenerActualPorIdAsync"/>): qué GraphSubscriptionId se
    /// renueva, se sustituye o se borra se decide con lo que hay ahora, no con lo que se cargó antes del cerrojo.
    /// </summary>
    Task<SuscripcionWebhook?> ObtenerActualPorConexionAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default);

    /// <summary>Suscripciones a menos de <paramref name="dentroDe"/> de expirar — para el job de renovación.</summary>
    Task<IReadOnlyList<SuscripcionWebhook>> ObtenerProximasAExpirarAsync(TimeSpan dentroDe, CancellationToken cancellationToken = default);

    void Agregar(SuscripcionWebhook suscripcion);
}
