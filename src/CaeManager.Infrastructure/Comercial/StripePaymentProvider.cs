using System.Text.Json;
using CaeManager.Application.Comercial.Common;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;

namespace CaeManager.Infrastructure.Comercial;

/// <summary>
/// Único <see cref="IPaymentProvider"/> real (Horizonte 1.7, "Billing mínimo
/// viable") — sobre el SDK oficial <c>Stripe.net</c>. No hay una
/// implementación de GoCardless todavía: construirla ahora, sin un solo
/// cliente de pago real, sería exactamente la estructura especulativa que el
/// § 0 principio 2 del plan de negocio prohíbe (regla de la señal externa).
/// Si SEPA/GoCardless llega a justificarse, la interfaz ya deja sitio para
/// añadirlo sin tocar Application ni Domain.
/// </summary>
public class StripePaymentProvider(IOptions<StripeOptions> opciones, ILogger<StripePaymentProvider> logger) : IPaymentProvider
{
    public string Codigo => "stripe";

    public Result<EventoWebhookProveedorDto> VerificarYLeerWebhook(string payloadJson, string? firma)
    {
        var config = opciones.Value;

        if (string.IsNullOrWhiteSpace(config.WebhookSecret))
        {
            logger.LogWarning("Webhook de Stripe recibido pero StripeOptions.WebhookSecret no está configurado.");
            return Result.Fallo<EventoWebhookProveedorDto>(Error.Crear(
                "PaymentProvider.NoConfigurado", "La integración de pagos no está disponible ahora mismo."));
        }

        if (string.IsNullOrWhiteSpace(firma))
        {
            return Result.Fallo<EventoWebhookProveedorDto>(Error.Crear(
                "PaymentProvider.FirmaInvalida", "Webhook rechazado: falta la firma."));
        }

        Event evento;
        try
        {
            // EventUtility.ConstructEvent verifica la firma HMAC del header
            // Stripe-Signature contra el payload crudo (sin deserializar
            // antes) y el WebhookSecret, y solo si es válida deserializa el
            // evento — es el patrón documentado por Stripe para no fiarse de
            // un payload cuya firma no se ha comprobado todavía.
            //
            // throwOnApiVersionMismatch: false — Hydra solo lee el tipo y el
            // id/customer/status de la Subscription embebida (y del estado
            // aplica el que reporta ObtenerSuscripcionAsync, no este), campos estables entre
            // versiones de la API de Stripe desde hace años; no hay motivo
            // para tumbar el procesamiento de un webhook legítimo por un
            // desajuste de versión que no afecta a nada de lo que se lee
            // aquí. Con el valor por defecto (true), además de ser
            // innecesario, Stripe.net 52.3.0 lanza NullReferenceException en vez de
            // StripeException cuando el evento no trae "api_version" —
            // comportamiento observado en pruebas, no solo teórico.
            evento = EventUtility.ConstructEvent(payloadJson, firma, config.WebhookSecret, throwOnApiVersionMismatch: false);
        }
        catch (Exception ex) when (ex is StripeException or NullReferenceException or FormatException or JsonException)
        {
            // Firma inválida y payload irreconocible devuelven el mismo
            // error a propósito (ver IPaymentProvider.VerificarYLeerWebhook):
            // no dar a quien intenta falsificar un webhook ninguna pista de
            // cuál de las dos cosas falló. El filtro de tipos (no un catch
            // genérico de Exception) es deliberado: un webhook malformado no
            // debe poder ocultar un bug real de Hydra detrás de un 401 —
            // solo las excepciones que Stripe.net documenta o se han
            // observado realmente al parsear/verificar un payload adverso
            // (NullReferenceException: ver el comentario de
            // throwOnApiVersionMismatch más arriba) quedan cubiertas aquí.
            // El SDK puede incluir datos de la petición en la excepción; no se
            // registra ni su mensaje ni su traza, solo el tipo para diagnóstico.
            logger.LogWarning("Webhook de Stripe rechazado: firma o payload inválidos ({TipoExcepcion}).",
                ex.GetType().Name);
            return Result.Fallo<EventoWebhookProveedorDto>(Error.Crear(
                "PaymentProvider.FirmaInvalida", "Webhook rechazado."));
        }

        // Solo los eventos de Subscription cambian el estado comercial de un
        // tenant. El estado de la propia Subscription (active/past_due/
        // unpaid/canceled) ya refleja cualquier fallo o resolución de cobro
        // asociado —Stripe emite customer.subscription.updated en cuanto
        // cambia ese status por un impago o su regularización— así que no
        // hace falta suscribirse también a invoice.payment_failed/
        // invoice.paid por separado; se reconocen y se responden 200 sin
        // acción (ver WebhookStripeEndpoints).
        if (evento.Data.Object is not Subscription suscripcion)
            return Result.Exito(new EventoWebhookProveedorDto(evento.Type, null));

        return Result.Exito(new EventoWebhookProveedorDto(evento.Type, AConDto(suscripcion)));
    }

    public async Task<Result<SuscripcionProveedorDto>> ObtenerSuscripcionAsync(
        string idSuscripcion, CancellationToken cancellationToken = default)
    {
        var config = opciones.Value;

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return Result.Fallo<SuscripcionProveedorDto>(Error.Crear(
                "PaymentProvider.NoConfigurado", "La integración de pagos no está disponible ahora mismo."));
        }

        try
        {
            var cliente = ManejadorHttp is null
                ? new StripeClient(config.ApiKey)
                : new StripeClient(config.ApiKey, httpClient: new SystemNetHttpClient(new HttpClient(ManejadorHttp)));
            var servicio = new SubscriptionService(cliente);
            var suscripcion = await servicio.GetAsync(idSuscripcion, cancellationToken: cancellationToken);
            return Result.Exito(AConDto(suscripcion));
        }
        catch (StripeException ex)
        {
            logger.LogError(ex, "Error consultando la suscripción {IdSuscripcion} en Stripe.", idSuscripcion);
            return Result.Fallo<SuscripcionProveedorDto>(Error.Crear(
                "PaymentProvider.ErrorApi", "No pudimos consultar esa suscripción en Stripe. Comprueba el Id e inténtalo de nuevo."));
        }
        catch (HttpRequestException ex)
        {
            // El SDK relanza el fallo de transporte tras agotar sus reintentos. Es un fallo
            // controlado del proveedor, no un error del servidor: el webhook pide reintento (503).
            logger.LogError(ex, "Stripe no respondió al consultar la suscripción {IdSuscripcion}.", idSuscripcion);
            return Result.Fallo<SuscripcionProveedorDto>(Error.Crear(
                "PaymentProvider.NoDisponible", "Stripe no responde ahora mismo. Inténtalo de nuevo en unos minutos."));
        }
    }

    /// <summary>Solo para los tests: sustituye el transporte HTTP del SDK de Stripe.</summary>
    internal HttpMessageHandler? ManejadorHttp { get; init; }

    private static SuscripcionProveedorDto AConDto(Subscription suscripcion) =>
        new(suscripcion.Id, suscripcion.CustomerId, MapearEstado(suscripcion.Status));

    /// <summary>
    /// Estados documentados de <c>Subscription.Status</c> en la API de
    /// Stripe: incomplete, incomplete_expired, trialing, active, past_due,
    /// canceled, unpaid, paused. "incomplete" (primer pago todavía sin
    /// completar, p. ej. pendiente de 3D Secure) y "paused" quedan como
    /// <see cref="EstadoSuscripcionProveedor.Desconocida"/> a propósito: no
    /// son ni un tenant activo ni un impago de un tenant que ya pagaba, y
    /// tratarlos como cualquiera de los dos sería una suposición — ver
    /// MapeoEstadoComercialTenant sobre por qué "no tocar el estado" es la
    /// respuesta segura.
    /// </summary>
    private static EstadoSuscripcionProveedor MapearEstado(string status) => status switch
    {
        "active" or "trialing" => EstadoSuscripcionProveedor.Activa,
        "past_due" => EstadoSuscripcionProveedor.EnPeriodoGracia,
        "unpaid" => EstadoSuscripcionProveedor.Impagada,
        "canceled" or "incomplete_expired" => EstadoSuscripcionProveedor.Cancelada,
        _ => EstadoSuscripcionProveedor.Desconocida,
    };
}
