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

    /// <summary>
    /// Nombre contra el que se valida el certificado TLS del servidor,
    /// cuando difiere de <see cref="Host"/> — el caso de dinahosting, cuyo
    /// certificado es un comodín compartido entre clientes
    /// (<c>*.correoseguro.dinaserver.com</c>) que nunca coincide con
    /// <c>mail.talveg.es</c>, el único nombre que resuelve por DNS. Si se
    /// deja vacío, se valida contra <see cref="Host"/> (comportamiento
    /// estándar).
    /// </summary>
    public string? NombreCertificadoTls { get; set; }

    /// <summary>
    /// URL pública de la aplicación (ej. https://app.talveg.es), para servir
    /// la franja de marca del sistema de correo como imagen (los clientes de
    /// correo la descargan por HTTP, no la reciben incrustada). Sin
    /// configurar, el correo se envía igual, con el nombre "TALVEG" como
    /// texto en vez de imagen — mismo patrón de fail-soft que
    /// AlertasPorCorreoOptions.UrlBase.
    /// </summary>
    public string? UrlBasePublica { get; set; }

    public bool EstaConfigurado =>
        !string.IsNullOrWhiteSpace(Host)
        && !string.IsNullOrWhiteSpace(Usuario)
        && !string.IsNullOrWhiteSpace(Contrasena)
        && !string.IsNullOrWhiteSpace(BuzonRemitente);
}
