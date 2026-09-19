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
    public async Task<Result> EnviarAsync(
        string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo,
        string? responderA = null,
        CancellationToken cancellationToken = default)
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

        var mensaje = ConstruirMensaje(destinatarioEmail, asunto, cuerpoHtml, tipo, responderA, config, logger);

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
    /// Arma el <see cref="MimeMessage"/> que sale por el hilo: es lo único
    /// que decide qué cabeceras lleva el correo, y está separado del envío
    /// para poder comprobarlo sin abrir una conexión SMTP — mismo criterio
    /// que <see cref="EnvolverEnPlantillaDeMarca"/> y
    /// <see cref="ValidarCertificadoServidor"/>.
    ///
    /// <para>
    /// <b>El <c>From</c> es siempre el buzón de TALVEG, con
    /// <paramref name="responderA"/> o sin él.</b> Es el buzón que tiene SPF
    /// y DKIM publicados: poner ahí el correo de un Gestor CAE haría que su
    /// dominio no autorizase a nuestro servidor, y el correo acabaría en spam
    /// o rechazado. Lo que cambia es <c>Reply-To</c>, que no lo firma nadie.
    /// </para>
    ///
    /// <para>
    /// El <c>Reply-To</c> se valida en vez de parsearse a secas
    /// (<see cref="EsDireccionUtilizable"/>) porque su valor viene de un
    /// <c>ApplicationUser</c>, no de un formulario: un correo mal formado ahí
    /// no puede tumbar el envío de la reclamación, que es la acción de
    /// negocio. Si no vale se sigue sin <c>Reply-To</c> y el pie deja de
    /// invitar a responder por sí solo — exactamente la misma rama que "esa
    /// cuenta no tiene correo".
    /// </para>
    /// </summary>
    internal static MimeMessage ConstruirMensaje(
        string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo,
        string? responderA, SmtpEmailOptions config, ILogger logger)
    {
        var mensaje = new MimeMessage();
        mensaje.From.Add(MailboxAddress.Parse(config.BuzonRemitente!));
        mensaje.To.Add(MailboxAddress.Parse(destinatarioEmail));

        var buzonDeRespuesta = EsDireccionUtilizable(responderA, out var direccion) ? direccion : null;

        if (buzonDeRespuesta is null && !string.IsNullOrWhiteSpace(responderA))
            logger.LogWarning("Se pidió un Reply-To que no es una dirección válida; el correo sale sin él.");

        if (buzonDeRespuesta is not null)
            mensaje.ReplyTo.Add(buzonDeRespuesta);

        mensaje.Subject = asunto;
        mensaje.Body = new TextPart(TextFormat.Html)
        {
            Text = EnvolverEnPlantillaDeMarca(cuerpoHtml, tipo, config, buzonDeRespuesta?.Address),
        };

        return mensaje;
    }

    /// <summary>
    /// Si <paramref name="valor"/> sirve de verdad como buzón de respuesta.
    ///
    /// <para>
    /// <b>No basta con <c>MailboxAddress.TryParse</c>.</b> MimeKit parsea en
    /// modo laxo por defecto y acepta una dirección sin dominio: con
    /// <c>"no-es-un-correo"</c> devuelve <c>true</c> y construye un buzón cuyo
    /// <c>Address</c> es esa misma cadena. Puesta en <c>Reply-To</c>, el
    /// destinatario vería un botón de responder que no lleva a ninguna parte —
    /// peor que no ofrecerlo, porque el pie sí le habría prometido que su
    /// respuesta llega. Medido al escribir el test, que salió rojo con
    /// <c>TryParse</c> a secas.
    /// </para>
    ///
    /// <para>
    /// La comprobación se queda en "hay parte local y hay dominio": no valida
    /// que el dominio exista ni que el buzón acepte correo —eso no se puede
    /// saber desde aquí— solo descarta lo que con seguridad no es una
    /// dirección.
    /// </para>
    /// </summary>
    internal static bool EsDireccionUtilizable(string? valor, out MailboxAddress? direccion)
    {
        direccion = null;

        if (string.IsNullOrWhiteSpace(valor) || !MailboxAddress.TryParse(valor, out var candidata))
            return false;

        var arroba = candidata.Address.LastIndexOf('@');
        if (arroba <= 0 || arroba == candidata.Address.Length - 1)
            return false;

        direccion = candidata;
        return true;
    }

    /// <summary>
    /// Sistema de correo TALVEG: envuelve el contenido que ya compone cada
    /// llamador (sin tocarlo) en la cabecera y el pie de marca, para que
    /// ningún sitio donde se arma un correo tenga que conocer este
    /// envoltorio ni repetirlo.
    ///
    /// <para>
    /// La franja de marca va como imagen (<c>UrlBasePublica</c> +
    /// <c>/img/correo/franja-marca.png</c>): ni Outlook ni Gmail dibujan SVG,
    /// y así se evita depender de una fuente serif que el cliente de correo
    /// no cargue. Sin <c>UrlBasePublica</c> configurada no hay URL pública
    /// que ofrecer, así que cae directamente al texto en Georgia — el mismo
    /// contenido que muestra el <c>alt</c> de la imagen cuando el cliente
    /// bloquea imágenes.
    /// </para>
    /// </summary>
    /// <param name="responderA">
    /// La dirección que ya se ha puesto en <c>Reply-To</c>, o <c>null</c> si
    /// el correo sale sin ella. Solo decide el pie de
    /// <see cref="TipoAvisoCorreo.Requerimiento"/>: con <c>Reply-To</c> el pie
    /// invita a responder y dice a quién llega la respuesta; sin él calla, que
    /// es lo que #698 corrigió. La dirección no se escribe en el cuerpo: ya
    /// viaja en la cabecera, y repetirla solo añade un dato personal más a un
    /// correo que sale fuera de la organización.
    /// </param>
    internal static string EnvolverEnPlantillaDeMarca(
        string cuerpoHtml, TipoAvisoCorreo tipo, SmtpEmailOptions config, string? responderA = null)
    {
        var franjaDeMarca = string.IsNullOrWhiteSpace(config.UrlBasePublica)
            ? """
              <div style="background:#122A21;padding:17px 22px 14px;font-family:Georgia,'Times New Roman',serif;">
                <div style="font-size:21px;color:#F2EEE1;letter-spacing:2.2px;">TALVEG</div>
                <p style="font-family:Arial,Helvetica,sans-serif;font-size:10.5px;color:#7E9C90;margin:6px 0 0;letter-spacing:.3px;">Coordinación de actividades empresariales</p>
              </div>
              """
            : $"""
               <div style="background:#122A21;padding:0;">
                 <img src="{config.UrlBasePublica!.TrimEnd('/')}/img/correo/franja-marca.png" width="600" alt="TALVEG — Coordinación de actividades empresariales"
                      style="display:block;width:100%;max-width:600px;height:auto;border:0;font-family:Georgia,'Times New Roman',serif;font-size:21px;color:#F2EEE1;letter-spacing:2.2px;">
               </div>
               """;

        var pie = tipo switch
        {
            TipoAvisoCorreo.Seguridad =>
                $"""
                 Este aviso se envía siempre por seguridad y no se puede desactivar.<br>
                 Si no has sido tú, avísanos respondiendo a <a href="mailto:{config.BuzonRemitente}">{config.BuzonRemitente}</a>. · <a href="https://talveg.es">talveg.es</a>
                 """,
            TipoAvisoCorreo.Informativo =>
                """
                Recibes este aviso por tu responsabilidad de coordinación en TALVEG. · <a href="https://talveg.es">talveg.es</a>
                """,
            TipoAvisoCorreo.Requerimiento when !string.IsNullOrWhiteSpace(responderA) =>
                """
                Este correo llega a través de TALVEG en nombre de quien te lo reclama. Puedes responder a este mensaje: tu respuesta le llegará directamente a esa persona. · <a href="https://talveg.es">talveg.es</a>
                """,
            TipoAvisoCorreo.Requerimiento =>
                """
                Este correo llega a través de TALVEG en nombre de quien te lo reclama. · <a href="https://talveg.es">talveg.es</a>
                """,
            _ =>
                """
                Si no esperabas este correo, puedes ignorarlo. · <a href="https://talveg.es">talveg.es</a>
                """,
        };

        return $$"""
                 <!doctype html>
                 <html>
                 <head>
                 <meta charset="utf-8">
                 <meta name="viewport" content="width=device-width,initial-scale=1">
                 <style>
                   body{margin:0;padding:0;background:#EDEDEB;}
                   .correo{max-width:600px;margin:0 auto;background:#FFFFFF;border:1px solid #E0E0DC;font-family:Arial,Helvetica,sans-serif;}
                   .cuerpo{padding:24px 22px 20px;font-size:14px;color:#2A322E;line-height:1.55;}
                   .cuerpo h3{font-family:Georgia,'Times New Roman',serif;font-weight:400;font-size:19px;color:#122A21;margin:0 0 10px;}
                   .cuerpo p{margin:0 0 13px;}
                   .cuerpo a{color:#122A21;}
                   .cuerpo table{width:100%;border-collapse:collapse;margin:4px 0 18px;}
                   .cuerpo td{padding:9px 0;border-bottom:1px solid #EDEEE9;font-size:13.5px;}
                   .pie{border-top:1px solid #EDEEE9;padding:14px 22px 18px;font-size:10.5px;color:#8A948E;line-height:1.55;}
                   .pie a{color:#8A948E;}
                 </style>
                 </head>
                 <body>
                 <div class="correo">
                   {{franjaDeMarca}}
                   <div class="cuerpo">
                     {{cuerpoHtml}}
                   </div>
                   <div class="pie">
                     {{pie}}
                   </div>
                 </div>
                 </body>
                 </html>
                 """;
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
