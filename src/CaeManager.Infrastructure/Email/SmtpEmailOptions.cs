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
    /// URL pública de la aplicación (ej. https://app.talveg.es). OBLIGATORIA fuera
    /// de Development (URL absoluta https): los enlaces de activación y de
    /// recuperación de contraseña se construyen con ella, nunca con el Host de la
    /// petición, y sin ella la aplicación no arranca (ValidadorUrlBasePublica,
    /// registrado con ValidateOnStart en Program.cs). En Development puede faltar y se usa el Host local. También
    /// permite servir la franja de marca del sistema de correo como imagen (los
    /// clientes de correo la descargan por HTTP, no la reciben incrustada).
    /// </summary>
    public string? UrlBasePublica { get; set; }

    /// <summary>
    /// Tope, en segundos, de cada operación contra el servidor (conectar,
    /// autenticar, enviar, desconectar) y del envío entero. Sin él, MailKit
    /// espera 120 s por operación: un servidor que acepta la conexión y no
    /// responde dejaba al llamador esperando minutos. Al vencer, el envío
    /// termina como el error de red de siempre (<c>Email.ErrorRed</c>), no
    /// como una excepción. Un valor no positivo se ignora y vale
    /// <see cref="TiempoMaximoPorDefectoSegundos"/>.
    /// </summary>
    public int TiempoMaximoSegundos { get; set; } = TiempoMaximoPorDefectoSegundos;

    public const int TiempoMaximoPorDefectoSegundos = 20;

    public bool EstaConfigurado =>
        !string.IsNullOrWhiteSpace(Host)
        && !string.IsNullOrWhiteSpace(Usuario)
        && !string.IsNullOrWhiteSpace(Contrasena)
        && !string.IsNullOrWhiteSpace(BuzonRemitente);
}
