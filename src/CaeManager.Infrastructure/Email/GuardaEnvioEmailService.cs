using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Email;

/// <summary>
/// Guarda de envío delante de <see cref="SmtpEmailService"/> (ver <see cref="EnvioCorreoOptions"/>).
/// Un correo bloqueado es un <see cref="Result"/> fallido, no un éxito simulado: quien llama sigue la
/// misma rama que con el SMTP caído, así que una reclamación no consta como enviada ni un aviso como
/// entregado. El rastro no lleva la dirección del destinatario.
/// </summary>
public sealed class GuardaEnvioEmailService(
    IEmailService interno,
    IOptions<EnvioCorreoOptions> opciones,
    ILogger<GuardaEnvioEmailService> logger) : IEmailService
{
    public Task<Result> EnviarAsync(
        string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo,
        string? responderA = null,
        CancellationToken cancellationToken = default)
    {
        var config = opciones.Value;

        if (config.PermiteEnvioA([destinatarioEmail]))
            return interno.EnviarAsync(destinatarioEmail, asunto, cuerpoHtml, tipo, responderA, cancellationToken);

        logger.LogWarning(EnvioCorreoOptions.TextoBloqueo + ": canal SMTP, tipo {Tipo}, modo {Modo}.", tipo, config.ModoEfectivo);
        return Task.FromResult(Result.Fallo(config.ErrorEnvioCerrado()));
    }
}
