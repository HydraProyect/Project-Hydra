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
/// ver <see cref="TipoAvisoCorreo"/> — y tiene un valor por defecto para que
/// los llamadores existentes seguirlo compilando sin cambios.
/// </para>
/// </summary>
public interface IEmailService
{
    Task<Result> EnviarAsync(
        string destinatarioEmail, string asunto, string cuerpoHtml,
        CancellationToken cancellationToken = default,
        TipoAvisoCorreo tipo = TipoAvisoCorreo.Transaccional);
}
