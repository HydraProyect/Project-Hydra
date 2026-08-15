namespace CaeManager.Infrastructure.Comercial;

/// <summary>
/// Configuración de <see cref="StripePaymentProvider"/>. Mismo patrón
/// "inerte por defecto" que <c>AnthropicOptions</c>/<c>MistralOcrOptions</c>
/// (ver src/CaeManager.Infrastructure/AsistenteIa): sin claves configuradas,
/// el proveedor de pagos no lanza ni bloquea el arranque — cada método
/// devuelve un <c>Result</c> fallido controlado.
/// </summary>
public class StripeOptions
{
    public const string SeccionConfiguracion = "Stripe";

    /// <summary>Clave secreta de la API de Stripe (sk_test_/sk_live_).</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Secreto de firma del endpoint de webhook (whsec_...), uno por
    /// endpoint configurado en el Dashboard de Stripe. Sin él, todo webhook
    /// entrante se rechaza — no hay forma segura de "aceptar sin verificar".
    /// </summary>
    public string? WebhookSecret { get; set; }
}
