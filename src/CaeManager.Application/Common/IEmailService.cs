using CaeManager.Domain.Common;

namespace CaeManager.Application.Common;

/// <summary>
/// Envía un correo. La implementación real vive en Infrastructure sobre SMTP
/// (buzón corporativo de dinahosting, info@talveg.es — ver Issue #2) —
/// Application solo conoce el contrato. Siempre "best effort" desde quien la
/// llama: un fallo de envío
/// nunca debe impedir la acción de negocio que lo dispara (crear un usuario
/// pendiente, asignarle un rol) — de ahí que devuelva Result en vez de
/// lanzar, para que el llamador decida solo si lo registra o lo ignora.
///
/// <para>
/// Sistema de correo TALVEG: <c>cuerpoHtml</c> sigue siendo solo el contenido
/// interior (lo que hoy ya componen los llamadores); la implementación lo
/// envuelve en la cabecera y el pie de marca antes de enviarlo, para que
/// ningún sitio donde se compone un correo tenga que conocer ese envoltorio.
/// <paramref name="tipo"/> decide únicamente qué pie institucional lleva —
/// ver <see cref="TipoAvisoCorreo"/> — y es obligatorio a propósito: con un
/// valor por defecto, un llamador nuevo que se olvide de clasificar su
/// correo compila igual y cae en <c>Transaccional</c> en silencio (así se
/// coló la reclamación de documentación con un pie de "puedes ignorarlo" —
/// ver <c>RegistroEnvioReclamacionService</c>).
/// <paramref name="encabezado"/> completa la franja de tipo (si el destinatario
/// tiene que hacer algo y de qué ámbito viene) y el texto de vista previa, y es
/// obligatorio por el mismo motivo. El contenido interior se compone con
/// <see cref="CorreoHtml"/>.
/// </para>
/// </summary>
public interface IEmailService
{
    /// <param name="responderA">
    /// Buzón al que debe volver la respuesta del destinatario, o <c>null</c>
    /// para que no haya ninguno. Va en la cabecera <c>Reply-To</c> y
    /// <b>nunca</b> sustituye al <c>From</c>: el remitente sigue siendo el
    /// buzón de TALVEG, que es el que tiene SPF y DKIM publicados — cambiar
    /// el <c>From</c> por el correo de una persona haría que su dominio no
    /// autorizase a nuestro servidor y el correo acabaría en spam o
    /// rechazado.
    ///
    /// <para>
    /// Existe por la decisión D3 (2026-09-19): la respuesta a una reclamación
    /// de documentación la recibe el Gestor CAE que la emite. Antes de esto,
    /// una reclamación enviada sin Conexión salía del buzón de TALVEG sin
    /// <c>Reply-To</c> y la respuesta de la Empresa contraparte se perdía ahí
    /// — por eso #698 quitó del pie de <see cref="TipoAvisoCorreo.Requerimiento"/>
    /// la invitación a responder. Con un <c>Reply-To</c> presente esa
    /// invitación vuelve, y solo entonces.
    /// </para>
    /// </param>
    Task<Result> EnviarAsync(
        string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo,
        EncabezadoCorreo encabezado,
        string? responderA = null,
        CancellationToken cancellationToken = default);
}
