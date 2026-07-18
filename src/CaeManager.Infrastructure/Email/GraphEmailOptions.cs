namespace CaeManager.Infrastructure.Email;

/// <summary>
/// Configuración del envío de correo vía Microsoft Graph (permisos de
/// aplicación, no delegados — no depende de que ningún usuario tenga sesión
/// iniciada, a diferencia de AzureAdOptions/SSO). Requiere un App
/// Registration en Entra ID con el permiso de aplicación "Mail.Send" y
/// consentimiento de administrador — puede ser el mismo App Registration
/// que el de SSO (compartiendo TenantId/ClientId/ClientSecret) o uno
/// distinto; esta sección es independiente a propósito, ver DEPLOY.md.
/// Sin las cuatro variables configuradas, el envío de correo queda inerte:
/// las acciones que lo disparan (usuario pendiente, asignación de rol)
/// siguen funcionando igual, solo que sin notificación — mismo patrón que
/// Sentry/Backups/Anthropic/AzureAd.
/// </summary>
public class GraphEmailOptions
{
    public const string SeccionConfiguracion = "Graph";

    public string? TenantId { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>UPN del buzón que envía el correo (ej. notificaciones@empresa.com) — debe existir como buzón real en el tenant.</summary>
    public string? BuzonRemitente { get; set; }

    public bool EstaConfigurado =>
        !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(BuzonRemitente);
}
