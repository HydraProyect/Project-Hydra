namespace CaeManager.Infrastructure.Email;

/// <summary>
/// Configuración del envío de correo vía SMTP (buzón corporativo alojado en
/// dinahosting, no Microsoft 365 — el MX de talveg.es resuelve a
/// mail.talveg.es, un servidor SMTP/IMAP tradicional, no a Exchange Online).
/// Sin las cinco variables configuradas, el envío de correo queda inerte: las
/// acciones que lo disparan (usuario pendiente, asignación de rol) siguen
/// funcionando igual, solo que sin notificación — mismo patrón que
/// Sentry/Backups/Anthropic/AzureAd.
/// </summary>
public class SmtpEmailOptions
{
    public const string SeccionConfiguracion = "Smtp";

    public string? Host { get; set; }

    public int Puerto { get; set; } = 587;

    public string? Usuario { get; set; }

    public string? Contrasena { get; set; }

    /// <summary>Dirección que envía el correo (ej. info@talveg.es).</summary>
    public string? BuzonRemitente { get; set; }

    public bool EstaConfigurado =>
        !string.IsNullOrWhiteSpace(Host)
        && !string.IsNullOrWhiteSpace(Usuario)
        && !string.IsNullOrWhiteSpace(Contrasena)
        && !string.IsNullOrWhiteSpace(BuzonRemitente);
}
