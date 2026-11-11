using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Tests.Integraciones;

public class SuscripcionWebhookRepositorioFalso : ISuscripcionWebhookRepository
{
    public List<SuscripcionWebhook> Suscripciones { get; } = [];

    /// <summary>Como <see cref="ConexionIntegracionRepositorioFalso.SimularRastreo"/>, para las suscripciones.</summary>
    public bool SimularRastreo { get; set; }

    private readonly Dictionary<Guid, SuscripcionWebhook> _rastreadas = [];

    public Task<SuscripcionWebhook?> ObtenerPorConexionAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default)
    {
        if (SimularRastreo && _rastreadas.TryGetValue(conexionIntegracionId, out var rastreada))
            return Task.FromResult<SuscripcionWebhook?>(rastreada);

        return Task.FromResult(Rastrear(Suscripciones.FirstOrDefault(s => s.ConexionIntegracionId == conexionIntegracionId)));
    }

    public Task<SuscripcionWebhook?> ObtenerActualPorConexionAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Rastrear(Suscripciones.FirstOrDefault(s => s.ConexionIntegracionId == conexionIntegracionId)));

    private SuscripcionWebhook? Rastrear(SuscripcionWebhook? suscripcion)
    {
        if (SimularRastreo && suscripcion is not null)
            _rastreadas[suscripcion.ConexionIntegracionId] = suscripcion;
        return suscripcion;
    }

    /// <summary>Otra operación sustituyó la suscripción de la conexión en la «base» (otra instancia, otro Id de Graph).</summary>
    public SuscripcionWebhook SustituirEnBase(Guid conexionIntegracionId, string graphSubscriptionId, DateTime expiracionUtc)
    {
        Suscripciones.RemoveAll(s => s.ConexionIntegracionId == conexionIntegracionId);
        var nueva = new SuscripcionWebhook(conexionIntegracionId, graphSubscriptionId, "estado", expiracionUtc);
        Suscripciones.Add(nueva);
        return nueva;
    }

    /// <summary>Como la consulta de EF, deja rastreadas (con <see cref="SimularRastreo"/>) las suscripciones del lote.</summary>
    public Task<IReadOnlyList<SuscripcionWebhook>> ObtenerProximasAExpirarAsync(TimeSpan dentroDe, CancellationToken cancellationToken = default)
    {
        var lote = Suscripciones.Where(s => s.FechaExpiracionUtc <= DateTime.UtcNow.Add(dentroDe)).ToList();
        foreach (var suscripcion in lote)
            Rastrear(suscripcion);

        AlListarProximasAExpirar?.Invoke();
        return Task.FromResult<IReadOnlyList<SuscripcionWebhook>>(lote);
    }

    /// <summary>Se invoca tras cargar el lote: otra operación escribe en la «base» antes de que se procese.</summary>
    public Action? AlListarProximasAExpirar { get; set; }

    public void Agregar(SuscripcionWebhook suscripcion) => Suscripciones.Add(suscripcion);
}
