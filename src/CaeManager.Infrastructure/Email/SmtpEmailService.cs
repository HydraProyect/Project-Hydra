using System.Net;
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
        EncabezadoCorreo encabezado,
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

        var mensaje = ConstruirMensaje(destinatarioEmail, asunto, cuerpoHtml, tipo, encabezado, responderA, config, logger);

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
        EncabezadoCorreo encabezado, string? responderA, SmtpEmailOptions config, ILogger logger)
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
            Text = EnvolverEnPlantillaDeMarca(cuerpoHtml, tipo, encabezado, config, buzonDeRespuesta?.Address),
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
        string cuerpoHtml, TipoAvisoCorreo tipo, EncabezadoCorreo encabezado, SmtpEmailOptions config,
        string? responderA = null)
    {
        // La celda lleva bgcolor verde además del PNG (que trae su propio verde):
        // con las imágenes bloqueadas queda el alt en crema sobre verde, y si el
        // cliente invierte colores por su cuenta el lockup no queda crema sobre blanco.
        var celdaDeMarca = string.IsNullOrWhiteSpace(config.UrlBasePublica)
            ? """
              <div style="padding:20px 24px;font-family:Georgia,'Times New Roman',serif;font-size:21px;line-height:1.35;color:#F2EEE1;letter-spacing:2.2px;">TALVEG<br><span style="font-family:Arial,Helvetica,sans-serif;font-size:11px;letter-spacing:.3px;color:#F2EEE1;">Coordinación de actividades empresariales</span></div>
              """
            : $"""
               <a href="https://talveg.es" style="text-decoration:none"><img src="{config.UrlBasePublica!.TrimEnd('/')}/img/correo/franja-marca.png" width="600" alt="TALVEG · Coordinación de actividades empresariales" style="display:block;width:100%;max-width:600px;height:auto;border:0;background:#122A21;font-family:Georgia,'Times New Roman',serif;font-size:18px;line-height:1.35;color:#F2EEE1"></a>
               """;

        var clase = tipo switch
        {
            TipoAvisoCorreo.Seguridad => "Seguridad",
            TipoAvisoCorreo.Requerimiento => "Requerimiento",
            _ => "Aviso",
        };
        var accion = encabezado.Accion switch
        {
            AccionEsperada.Accion => "Acción requerida",
            AccionEsperada.Revision => "Requiere revisión",
            AccionEsperada.Respuesta => "Respuesta necesaria",
            _ => "No requiere acción",
        };
        // Requerimiento va en verde de marca con crema (también en noche, ligeramente más oscuro);
        // el resto en la caja de acento celeste. Es lo que distingue "me reclaman algo" de "me avisan".
        var claseFranja = tipo == TipoAvisoCorreo.Requerimiento ? "franja-req" : "franja";
        var fondoFranja = tipo == TipoAvisoCorreo.Requerimiento ? "#122A21" : "#E9F3F0";
        var colorFranja = tipo == TipoAvisoCorreo.Requerimiento ? "#F2EEE1" : "#122A21";
        var bordeFranja = tipo == TipoAvisoCorreo.Requerimiento ? "#122A21" : "#E0E3DE";
        var estiloFranja =
            $"background:{fondoFranja};border-bottom:1px solid {bordeFranja};padding:11px 28px;font-family:Arial,Helvetica,sans-serif;color:{colorFranja};";
        var preheader = CorreoHtml.Codificar(encabezado.Preheader);
        var textoClase = CorreoHtml.Codificar($"{clase} · {accion}");
        var textoAmbito = CorreoHtml.Codificar(encabezado.Ambito);

        var pie = tipo switch
        {
            TipoAvisoCorreo.Seguridad =>
                $"""
                 Este aviso se envía siempre por seguridad y no se puede desactivar.<br>
                 Si no has sido tú, avísanos en <a href="mailto:{config.BuzonRemitente}">{config.BuzonRemitente}</a>. · <a href="https://talveg.es">talveg.es</a>
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

        // Los enlaces del pie llevan el color en línea: la hoja <style> solo aporta el modo noche.
        pie = pie.Replace("<a href=", "<a class=\"lnk\" style=\"color:#122A21\" href=", StringComparison.Ordinal);

        return $$"""
                 <!doctype html>
                 <html lang="es">
                 <head>
                 <meta charset="utf-8">
                 <meta name="viewport" content="width=device-width,initial-scale=1">
                 <meta name="color-scheme" content="light dark">
                 <meta name="supported-color-schemes" content="light dark">
                 <title>TALVEG</title>
                 <style>
                   :root { color-scheme: light dark; supported-color-schemes: light dark; }
                   @media only screen and (max-width:480px) {
                     .fx { display:block !important; width:100% !important; text-align:left !important; }
                     .pad { padding-left:18px !important; padding-right:18px !important; }
                     .tit { font-size:22px !important; }
                   }
                   @media (prefers-color-scheme: dark) {
                     .pagina { background:#0E1512 !important; }
                     .sup    { background:#18221E !important; }
                     .txt    { color:#DDE5E0 !important; }
                     .tit    { color:#F2EEE1 !important; }
                     .suave  { color:#9DAEA5 !important; }
                     .caja   { background:#1E3129 !important; color:#DDE5E0 !important; }
                     .franja { background:#1E3129 !important; border-color:#27362F !important; color:#8FC7BC !important; }
                     .franja-req { background:#0B1410 !important; border-color:#0B1410 !important; }
                     .cta-td { background:#8FC7BC !important; border-color:#F2EEE1 !important; }
                     .cta-a  { color:#0D1F18 !important; }
                     .pie    { background:#101815 !important; border-color:#27362F !important; color:#9DAEA5 !important; }
                     .lnk    { color:#8FC7BC !important; }
                     .venc   { color:#FF8A80 !important; }
                     .urg    { color:#F2C265 !important; }
                     .aviso  { background:#3A2F0D !important; border-color:#F2C265 !important; color:#F7D488 !important; }
                   }
                   /* Outlook.com con inversión propia: al menos el botón conserva su forma */
                   [data-ogsc] .cta-td { background:#122A21 !important; border-color:#8FC7BC !important; }
                   [data-ogsc] .cta-a  { color:#F2EEE1 !important; }
                 </style>
                 </head>
                 <body style="margin:0;padding:0;background:#E7E9E5">
                 <div style="display:none;max-height:0;overflow:hidden;font-size:1px;line-height:1px;color:#E7E9E5;opacity:0">{{preheader}}</div>
                 <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" bgcolor="#E7E9E5" class="pagina" style="background:#E7E9E5;border-collapse:collapse"><tr><td align="center" style="padding:14px">
                 <table role="presentation" width="600" cellpadding="0" cellspacing="0" border="0" bgcolor="#FBFAF6" class="sup" style="width:100%;max-width:600px;border-collapse:collapse;background:#FBFAF6">
                 <tr><td colspan="2" bgcolor="#122A21" style="background:#122A21;font-size:0;line-height:0">{{celdaDeMarca}}</td></tr>
                 <tr><td colspan="2" bgcolor="#8FC7BC" style="background:#8FC7BC;height:4px;line-height:4px;font-size:0">&nbsp;</td></tr>
                 <tr>
                   <td class="{{claseFranja}} fx" bgcolor="{{fondoFranja}}" style="{{estiloFranja}}font-size:11.5px;font-weight:bold;letter-spacing:1.2px;text-transform:uppercase">{{textoClase}}</td>
                   <td class="{{claseFranja}} fx" align="right" bgcolor="{{fondoFranja}}" style="{{estiloFranja}}font-size:12px">{{textoAmbito}}</td>
                 </tr>
                 <tr><td colspan="2" class="pad" style="padding:30px 28px 10px">{{cuerpoHtml}}</td></tr>
                 <tr><td colspan="2" class="pie pad" bgcolor="#F1F2EE" style="background:#F1F2EE;border-top:1px solid #E0E3DE;padding:16px 28px 22px;font-family:Arial,Helvetica,sans-serif;font-size:11.5px;line-height:1.6;color:#4F5C56">{{pie}}</td></tr>
                 </table>
                 </td></tr></table>
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
