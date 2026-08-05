namespace CaeManager.Application.Integraciones;

public record VerificacionWebhookWhatsAppDto(bool Verificado, Guid? TenantId, Guid? ConexionIntegracionId);

/// <summary>
/// Resolución de tenant para el webhook de WhatsApp (docs/MULTITENANCY.md
/// § 8, tercer modo). A diferencia de Microsoft 365 (una URL por conexión),
/// Meta configura UNA sola URL de callback por app: la línea receptora se
/// identifica por el <c>phone_number_id</c> del payload — y solo DESPUÉS de
/// que el endpoint haya verificado la firma HMAC global
/// (X-Hub-Signature-256) que autentica a Meta. Este resolver no autentica
/// nada: solo traduce una línea ya autenticada a su tenant.
/// </summary>
public interface IWebhookWhatsAppTenantResolver
{
    Task<VerificacionWebhookWhatsAppDto> ResolverPorPhoneNumberIdAsync(string phoneNumberId, CancellationToken cancellationToken);
}
