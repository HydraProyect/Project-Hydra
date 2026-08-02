using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Tests.Integraciones;

public class SuscripcionWebhookRepositorioFalso : ISuscripcionWebhookRepository
{
    public List<SuscripcionWebhook> Suscripciones { get; } = [];

    public Task<SuscripcionWebhook?> ObtenerPorConexionAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Suscripciones.FirstOrDefault(s => s.ConexionIntegracionId == conexionIntegracionId));

    public Task<IReadOnlyList<SuscripcionWebhook>> ObtenerProximasAExpirarAsync(TimeSpan dentroDe, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SuscripcionWebhook>>(
            Suscripciones.Where(s => s.FechaExpiracionUtc <= DateTime.UtcNow.Add(dentroDe)).ToList());

    public void Agregar(SuscripcionWebhook suscripcion) => Suscripciones.Add(suscripcion);
}
