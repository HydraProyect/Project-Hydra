namespace CaeManager.Application.Integraciones;

public record VerificacionWebhookDto(bool Verificado, Guid? TenantId);

/// <summary>
/// Tercer modo de resolución de tenant (docs/MULTITENANCY.md § 8): sin
/// sesión ni claim, un webhook entrante se resuelve por identificador de
/// recurso (<paramref name="conexionIntegracionId"/> en la URL) +
/// verificación de secreto — nunca se confía en el TenantId implícito de la
/// conexión hasta que el ClientState recibido coincide con el guardado.
///
/// <paramref name="subscriptionIdRecibido"/> es una segunda comprobación
/// (auditoría módulo 6): además del secreto, exige que el payload traiga el
/// Id de suscripción de Graph activo para esa conexión — reduce lo que un
/// ClientState filtrado, por sí solo, podría hacer aceptar.
/// </summary>
public interface IWebhookTenantResolver
{
    Task<VerificacionWebhookDto> VerificarAsync(
        Guid conexionIntegracionId, string clientStateRecibido, string? subscriptionIdRecibido, CancellationToken cancellationToken);
}
