using System.Net;
using System.Net.Mail;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Email;

/// <summary>
/// Envía correo por SMTP contra el buzón corporativo de dinahosting
/// (info@talveg.es). Crea un <see cref="SmtpClient"/> nuevo en cada envío en
/// vez de mantenerlo compartido: mismo criterio que <c>GraphEmailService</c>
/// tenía con el token — el volumen (uso interno, notificaciones puntuales) no
/// justifica gestionar el ciclo de vida de una conexión persistente.
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

        using var cliente = new SmtpClient(config.Host, config.Puerto)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(config.Usuario, config.Contrasena),
        };

        using var mensaje = new MailMessage(config.BuzonRemitente!, destinatarioEmail, asunto, cuerpoHtml)
        {
            IsBodyHtml = true,
        };

        try
        {
            await cliente.SendMailAsync(mensaje, cancellationToken);
            return Result.Exito();
        }
        catch (SmtpException ex)
        {
            logger.LogError(ex, "El servidor SMTP rechazó el envío de un correo: {StatusCode}.", ex.StatusCode);
            return Result.Fallo(Error.Crear("Email.ErrorApi", "No pudimos enviar el correo."));
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException)
        {
            logger.LogError(ex, "Fallo de red al enviar correo vía SMTP.");
            return Result.Fallo(Error.Crear("Email.ErrorRed", "No pudimos enviar el correo."));
        }
    }
}
