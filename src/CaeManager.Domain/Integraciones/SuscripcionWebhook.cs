using CaeManager.Domain.Common;

namespace CaeManager.Domain.Integraciones;

/// <summary>
/// Satélite 1:1 de <see cref="ConexionIntegracion"/>: la suscripción de
/// notificaciones de Microsoft Graph sobre el buzón. <see cref="ClientState"/>
/// es el secreto que Hydra elige al crear la suscripción y que Graph
/// devuelve sin cambios en cada notificación — es lo que el endpoint de
/// webhook compara para confiar en el payload (ver docs/MULTITENANCY.md § 8,
/// tercer modo de resolución de tenant). No es una firma criptográfica sobre
/// el cuerpo — Graph no firma notificaciones, solo hace eco del secreto
/// compartido.
/// </summary>
public class SuscripcionWebhook : EntidadConTenant
{
    public const int LongitudMaximaGraphSubscriptionId = 100;
    public const int LongitudMaximaClientState = 200;

    public Guid ConexionIntegracionId { get; private set; }
    public string GraphSubscriptionId { get; private set; } = string.Empty;
    public string ClientState { get; private set; } = string.Empty;
    public DateTime FechaExpiracionUtc { get; private set; }

    private SuscripcionWebhook()
    {
    }

    public SuscripcionWebhook(Guid conexionIntegracionId, string graphSubscriptionId, string clientState, DateTime fechaExpiracionUtc)
    {
        if (conexionIntegracionId == Guid.Empty)
            throw new ArgumentException("La suscripción debe pertenecer a una conexión.", nameof(conexionIntegracionId));

        ConexionIntegracionId = conexionIntegracionId;
        EstablecerGraphSubscriptionId(graphSubscriptionId);
        EstablecerClientState(clientState);
        FechaExpiracionUtc = fechaExpiracionUtc;
    }

    /// <summary>
    /// Cubre tanto la renovación normal (PATCH, mismo Id de Graph) como el
    /// caso en que la renovación falló y hubo que crear una suscripción
    /// nueva (Id de Graph distinto) — el llamador decide cuál pasar, esta
    /// entidad no distingue los dos casos.
    /// </summary>
    public void ActualizarTrasRenovacion(string graphSubscriptionId, DateTime nuevaExpiracionUtc)
    {
        EstablecerGraphSubscriptionId(graphSubscriptionId);
        FechaExpiracionUtc = nuevaExpiracionUtc;
    }

    private void EstablecerGraphSubscriptionId(string graphSubscriptionId)
    {
        if (string.IsNullOrWhiteSpace(graphSubscriptionId))
            throw new ArgumentException("La suscripción debe tener un Id de Graph.", nameof(graphSubscriptionId));

        var normalizado = graphSubscriptionId.Trim();
        if (normalizado.Length > LongitudMaximaGraphSubscriptionId)
            throw new ArgumentException($"El Id de Graph no puede superar {LongitudMaximaGraphSubscriptionId} caracteres.", nameof(graphSubscriptionId));

        GraphSubscriptionId = normalizado;
    }

    private void EstablecerClientState(string clientState)
    {
        if (string.IsNullOrWhiteSpace(clientState))
            throw new ArgumentException("La suscripción debe tener un ClientState.", nameof(clientState));

        if (clientState.Length > LongitudMaximaClientState)
            throw new ArgumentException($"El ClientState no puede superar {LongitudMaximaClientState} caracteres.", nameof(clientState));

        ClientState = clientState;
    }
}
