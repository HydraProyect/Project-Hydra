using CaeManager.Domain.Common;

namespace CaeManager.Application.Common;

/// <summary>
/// Envía un correo. La implementación real vive en Infrastructure sobre
/// Microsoft Graph (mismo buzón corporativo ya evaluado para el email de
/// bienvenida, ver ROADMAP.md e Issue #2) — Application solo conoce el
/// contrato. Siempre "best effort" desde quien la llama: un fallo de envío
/// nunca debe impedir la acción de negocio que lo dispara (crear un usuario
/// pendiente, asignarle un rol) — de ahí que devuelva Result en vez de
/// lanzar, para que el llamador decida solo si lo registra o lo ignora.
/// </summary>
public interface IEmailService
{
    Task<Result> EnviarAsync(string destinatarioEmail, string asunto, string cuerpoHtml, CancellationToken cancellationToken = default);
}
