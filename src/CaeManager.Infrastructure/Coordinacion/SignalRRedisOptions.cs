namespace CaeManager.Infrastructure.Coordinacion;

/// <summary>
/// Backplane de Redis para SignalR (P3-30 de docs/business/MATURITY_REVIEW.md):
/// sin él, un circuito de Blazor Server abierto contra la réplica A no puede
/// recibir mensajes si el balanceador manda una reconexión a la réplica B —
/// se pierde el circuito aunque la sesión HTTP siga viva.
///
/// Apagado por defecto, mismo patrón que <c>AlmacenamientoS3</c>/
/// <c>DataProtection:S3</c>: sin Redis provisionado, SignalR sigue con su
/// backplane en memoria del proceso — correcto para una sola réplica.
/// </summary>
public class SignalRRedisOptions
{
    public const string SeccionConfiguracion = "SignalR:Redis";

    public bool Activo { get; set; }

    public string? CadenaConexion { get; set; }

    public bool EstaConfigurado => Activo && !string.IsNullOrWhiteSpace(CadenaConexion);
}
