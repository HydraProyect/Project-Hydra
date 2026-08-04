namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// Credenciales de la app de Meta para WhatsApp Cloud API — globales de
/// plataforma (docs/MULTITENANCY.md § 7: la configuración de la app es
/// global; la credencial POR LÍNEA es <c>LineaWhatsApp.TokenAcceso</c>,
/// cifrada en BD por tenant). AppSecret firma cada POST del webhook
/// (X-Hub-Signature-256, HMAC-SHA256 del cuerpo crudo); VerifyToken es el
/// valor que Meta reenvía en el GET de verificación del callback.
///
/// Apagado por defecto, mismo patrón que el resto de integraciones: sin
/// estas dos claves el hosted service de ingesta no se registra y el
/// webhook responde 403/401 a todo.
/// </summary>
public class WhatsAppCloudApiOptions
{
    public const string SeccionConfiguracion = "Integraciones:WhatsApp";

    public string? AppSecret { get; set; }

    public string? VerifyToken { get; set; }

    /// <summary>Versión de Graph API de Meta usada en todas las llamadas salientes.</summary>
    public string VersionApi { get; set; } = "v23.0";

    public bool EstaConfigurado => !string.IsNullOrWhiteSpace(AppSecret) && !string.IsNullOrWhiteSpace(VerifyToken);
}
