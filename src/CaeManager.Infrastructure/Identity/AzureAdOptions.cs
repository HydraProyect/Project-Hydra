namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Configuración del inicio de sesión corporativo vía Microsoft Entra ID
/// (OpenID Connect). Sin TenantId/ClientId/ClientSecret, el método de login
/// externo queda inerte (mismo patrón que Sentry/Backups/Anthropic): el
/// botón "Iniciar sesión con Microsoft" ni se muestra, y el login local
/// sigue funcionando exactamente igual que hoy — ver
/// RestriccionLoginLocalClaimsTransformation y ARCHITECTURE.md.
/// </summary>
public class AzureAdOptions
{
    public const string SeccionConfiguracion = "AzureAd";

    /// <summary>Id del tenant de Entra ID de la empresa — restringe el login a cuentas de esa organización, nunca "cualquier cuenta Microsoft".</summary>
    public string? TenantId { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string Instance { get; set; } = "https://login.microsoftonline.com/";

    public bool EstaConfigurado =>
        !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
