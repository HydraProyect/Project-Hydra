namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// App Registration de Entra ID para el conector de Microsoft 365 (P3-33 de
/// docs/business/MATURITY_REVIEW.md — buzón de correo conectado por
/// Cliente/Tenant, ver ARQUITECTURA-INTEGRACIONES.md § 12). A diferencia de
/// <c>AzureAdOptions</c>/<c>GraphEmailOptions</c> (single-tenant, atados a
/// la propia organización de Hydra), este App Registration debe ser
/// **multi-tenant** ("cuentas de cualquier organización") porque cada buzón
/// conectado pertenece al Entra ID de un cliente distinto — por eso el
/// endpoint de autorización usa <c>common</c>, no un <c>TenantId</c> fijo.
/// Permisos delegados necesarios: <c>Mail.Read</c>, <c>Mail.Send</c>,
/// <c>offline_access</c> (para el refresh token) — ver DEPLOY.md.
///
/// Apagado por defecto, mismo patrón que el resto de integraciones
/// opcionales: sin credenciales, la pantalla de Conexiones sigue mostrando
/// el botón "Conectar" pero el flujo falla con un aviso explícito en vez de
/// arrancar el proceso de arranque roto.
/// </summary>
public class Microsoft365GraphOptions
{
    public const string SeccionConfiguracion = "Integraciones:Microsoft365";

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>
    /// URL pública canónica de la aplicación (ej. https://app.talveg.es),
    /// sin barra final. El redirect_uri de OAuth y la URL de notificación de
    /// la suscripción de Graph se construyen SIEMPRE desde aquí, nunca desde
    /// <c>HttpRequest.Scheme</c>/<c>Host</c> (auditoría módulo 6): una
    /// configuración de proxy/host filtering incorrecta permitiría derivar
    /// esas URLs de seguridad desde una cabecera Host manipulada por quien
    /// hace la petición.
    /// </summary>
    public string? UrlPublicaBase { get; set; }

    public bool EstaConfigurado =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) && !string.IsNullOrWhiteSpace(UrlPublicaBase);
}
