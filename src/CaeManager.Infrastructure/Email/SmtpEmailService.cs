using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;
using ExcepcionAutenticacionTls = System.Security.Authentication.AuthenticationException;
using ExcepcionAutenticacionSmtp = MailKit.Security.AuthenticationException;

namespace CaeManager.Infrastructure.Email;

/// <summary>
/// Envía correo por SMTP contra el buzón corporativo de dinahosting
/// (info@talveg.es). Usa MailKit en vez de <see cref="System.Net.Mail.SmtpClient"/>
/// (obsoleto) porque necesitamos validar el certificado TLS del servidor por
/// instancia: el certificado real de dinahosting es un comodín compartido
/// (<c>*.correoseguro.dinaserver.com</c>) que nunca coincide con el host al
/// que nos conectamos (<c>mail.talveg.es</c>, el único nombre que resuelve),
/// y <see cref="System.Net.Mail.SmtpClient"/> no permite sustituir esa
/// comprobación salvo con el callback estático y global de
/// <see cref="System.Net.ServicePointManager"/>. Crea un cliente nuevo en
/// cada envío en vez de mantenerlo compartido: mismo criterio que
/// <c>GraphEmailService</c> tenía con el token — el volumen (uso interno,
/// notificaciones puntuales) no justifica gestionar el ciclo de vida de una
/// conexión persistente.
/// </summary>
public class SmtpEmailService(
    IOptions<SmtpEmailOptions> opciones,
    ILogger<SmtpEmailService> logger) : IEmailService
{
    public async Task<Result> EnviarAsync(string destinatarioEmail, string asunto, string cuerpoHtml, CancellationToken cancellationToken = default)
    {
        var config = opciones.Value;

        if (!config.EstaConfigurado)
        {
            logger.LogError("Se intentó enviar un correo sin Smtp:* configurado.");
            return Result.Fallo(Error.Crear("Email.NoConfigurado", "El envío de correo no está configurado."));
        }

        using var cliente = new SmtpClient
        {
            ServerCertificateValidationCallback = (_, certificado, cadena, erroresPolitica) =>
                ValidarCertificadoServidor(config, certificado, cadena, erroresPolitica),
        };

        var mensaje = new MimeMessage();
        mensaje.From.Add(MailboxAddress.Parse(config.BuzonRemitente!));
        mensaje.To.Add(MailboxAddress.Parse(destinatarioEmail));
        mensaje.Subject = asunto;
        mensaje.Body = new TextPart(TextFormat.Html) { Text = cuerpoHtml };

        try
        {
            await cliente.ConnectAsync(config.Host!, config.Puerto, SecureSocketOptions.StartTls, cancellationToken);
            await cliente.AuthenticateAsync(config.Usuario!, config.Contrasena!, cancellationToken);
            await cliente.SendAsync(mensaje, cancellationToken);
            await cliente.DisconnectAsync(true, cancellationToken);
            return Result.Exito();
        }
        catch (SmtpCommandException ex)
        {
            logger.LogError(ex, "El servidor SMTP rechazó el envío de un correo: {StatusCode}.", ex.StatusCode);
            return Result.Fallo(Error.Crear("Email.ErrorApi", "No pudimos enviar el correo."));
        }
        catch (ExcepcionAutenticacionSmtp ex)
        {
            logger.LogError(ex, "El servidor SMTP rechazó las credenciales configuradas.");
            return Result.Fallo(Error.Crear("Email.ErrorApi", "No pudimos enviar el correo."));
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or SmtpProtocolException or SslHandshakeException or ExcepcionAutenticacionTls)
        {
            logger.LogError(ex, "Fallo de red o TLS al enviar correo vía SMTP.");
            return Result.Fallo(Error.Crear("Email.ErrorRed", "No pudimos enviar el correo."));
        }
    }

    /// <summary>
    /// Valida el certificado del servidor por su SAN real
    /// (<see cref="SmtpEmailOptions.NombreCertificadoTls"/>, o <c>Host</c> si
    /// no se configuró uno distinto) en vez de por el host de conexión: ver
    /// el comentario de clase. Solo relaja la comprobación de nombre — un
    /// error de cadena, revocación o vigencia sigue rechazando el certificado.
    /// </summary>
    internal static bool ValidarCertificadoServidor(SmtpEmailOptions config, X509Certificate? certificado, X509Chain? cadena, SslPolicyErrors erroresPolitica)
    {
        if (erroresPolitica == SslPolicyErrors.None)
        {
            return true;
        }

        if (erroresPolitica != SslPolicyErrors.RemoteCertificateNameMismatch || certificado is null)
        {
            return false;
        }

        var nombreEsperado = string.IsNullOrWhiteSpace(config.NombreCertificadoTls) ? config.Host : config.NombreCertificadoTls;
        if (string.IsNullOrWhiteSpace(nombreEsperado))
        {
            return false;
        }

        using var certificadoX509 = new X509Certificate2(certificado);
        var extensionSan = certificadoX509.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();

        return extensionSan is not null && extensionSan.EnumerateDnsNames().Any(nombre => CoincideDominio(nombre, nombreEsperado));
    }

    private static bool CoincideDominio(string nombreCertificado, string nombreEsperado)
    {
        if (string.Equals(nombreCertificado, nombreEsperado, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!nombreCertificado.StartsWith("*.", StringComparison.Ordinal))
        {
            return false;
        }

        var sufijo = nombreCertificado[1..];
        return nombreEsperado.Length > sufijo.Length && nombreEsperado.EndsWith(sufijo, StringComparison.OrdinalIgnoreCase);
    }
}
