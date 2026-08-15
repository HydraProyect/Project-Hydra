using CaeManager.Domain.Common;

namespace CaeManager.Application.Comercial.Common;

/// <summary>
/// Frontera entre el "Commercial Domain" de Hydra (plan, estado de
/// suscripción por tenant) y el motor de facturación real, que Hydra
/// deliberadamente no construye (plan de negocio § 1.7). Hoy solo tiene una
/// implementación real (<c>StripePaymentProvider</c>, Infrastructure) —
/// existe como interfaz, y no como llamada directa al SDK de Stripe desde
/// Application, únicamente para que un proveedor SEPA nativo (GoCardless)
/// pueda añadirse más adelante sin tocar el dominio ni el gate, si el
/// negocio llega a justificarlo. No hay un segundo proveedor hoy — construir
/// uno sin clientes de pago sería sobre-ingeniería (mismo criterio que
/// docs de arquitectura de IA: "primero rastro, luego automatización").
///
/// Mismo patrón "inerte por defecto" que <c>IDocumentAIProvider</c>: sin
/// clave configurada, cada método falla con un <see cref="Error"/>
/// controlado — nunca lanza ni bloquea el arranque.
/// </summary>
public interface IPaymentProvider
{
    /// <summary>Identificador corto del proveedor ("stripe") — mismo patrón que IDocumentAIProvider.Codigo.</summary>
    string Codigo { get; }

    /// <summary>
    /// Verifica la firma de un webhook entrante contra el secreto
    /// configurado y, solo si es válida, interpreta el evento. El fallo por
    /// firma inválida y el fallo por payload irreconocible son
    /// indistinguibles a propósito para quien llama — ver
    /// <c>StripePaymentProvider</c> para el porqué (no dar pistas a quien
    /// intenta falsificar webhooks).
    /// </summary>
    Result<EventoWebhookProveedorDto> VerificarYLeerWebhook(string payloadJson, string? firma);

    /// <summary>
    /// Consulta el estado actual de una Subscription directamente en Stripe
    /// — usado tanto en el alta manual (para no dar de alta a ciegas lo que
    /// el operador tecleó, ver RegistrarSuscripcionTenantCommand) como en la
    /// resincronización manual desde el panel de plataforma.
    /// </summary>
    Task<Result<SuscripcionProveedorDto>> ObtenerSuscripcionAsync(string idSuscripcion, CancellationToken cancellationToken = default);
}

/// <summary>Estado de una Subscription tal como lo reporta el proveedor de pago — anterior a mapearlo a <see cref="Domain.Tenants.EstadoComercialTenant"/> (ver <c>MapeoEstadoComercialTenant</c>).</summary>
public enum EstadoSuscripcionProveedor
{
    /// <summary>Estado del proveedor no reconocido/mapeado — se trata como "no tocar el estado comercial actual" (ver MapeoEstadoComercialTenant), nunca como impago.</summary>
    Desconocida,
    Activa,
    EnPeriodoGracia,
    Impagada,
    Cancelada,
}

public record SuscripcionProveedorDto(string IdSuscripcion, string IdCliente, EstadoSuscripcionProveedor Estado);

/// <summary>
/// <see cref="Suscripcion"/> es null para tipos de evento que Hydra recibe
/// pero no necesita actuar (p. ej. eventos de factura, ver
/// <c>StripePaymentProvider</c>) — el endpoint de webhook los reconoce y
/// responde 200 sin más, en vez de tratarlos como error.
/// </summary>
public record EventoWebhookProveedorDto(string TipoEvento, SuscripcionProveedorDto? Suscripcion);
