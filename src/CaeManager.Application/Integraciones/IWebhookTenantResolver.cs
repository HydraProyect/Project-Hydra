namespace CaeManager.Application.Integraciones;

public record VerificacionWebhookDto(bool Verificado, Guid? TenantId);

/// <summary>
/// Tercer modo de resolución de tenant (docs/MULTITENANCY.md § 8): sin
/// sesión ni claim, un webhook entrante se resuelve por identificador de
/// recurso (<paramref name="conexionIntegracionId"/> en la URL) +
/// verificación de secreto — nunca se confía en el TenantId implícito de la
/// conexión hasta que el ClientState recibido coincide con el guardado.
/// </summary>
public interface IWebhookTenantResolver
{
    Task<VerificacionWebhookDto> VerificarAsync(
        Guid conexionIntegracionId, string clientStateRecibido, CancellationToken cancellationToken);
}
