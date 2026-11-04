using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comercial.Common;

/// <summary>Qué hizo <see cref="IProcesadorWebhookSuscripcion"/> con un evento ya verificado.</summary>
public enum ResultadoWebhookSuscripcion
{
    /// <summary>Evento sin Subscription (factura, cliente…): no cambia el estado comercial.</summary>
    SinSuscripcion,

    /// <summary>La Subscription no está vinculada a ningún Tenant todavía.</summary>
    SinTenantVinculado,

    /// <summary>La Subscription resuelve al Tenant de plataforma: deriva de datos, se ignora sin mutar.</summary>
    TenantNoSuscribible,

    /// <summary>Stripe devolvió un estado que TALVEG no sabe mapear: se ignora sin mutar.</summary>
    EstadoNoReconocido,

    /// <summary>No se pudo consultar el estado actual en Stripe: el webhook debe reintentarse.</summary>
    ProveedorNoDisponible,

    /// <summary>Otro procesamiento tiene el candado de la Subscription: el webhook debe reintentarse.</summary>
    SuscripcionOcupada,

    /// <summary>El estado actual de la Subscription ya era el del Tenant: no se escribe nada.</summary>
    SinCambios,

    /// <summary>Estado comercial actualizado con el estado actual de la Subscription.</summary>
    Aplicado,
}

/// <param name="CodigoError">Código del <see cref="Error"/> del proveedor cuando <see cref="Resultado"/> es <see cref="ResultadoWebhookSuscripcion.ProveedorNoDisponible"/>, para el log.</param>
public record ProcesamientoWebhookSuscripcion(ResultadoWebhookSuscripcion Resultado, string? CodigoError = null);

/// <summary>Frontera del endpoint de webhook con el procesamiento, para probar su traducción a HTTP aparte.</summary>
public interface IProcesadorWebhookSuscripcion
{
    Task<ProcesamientoWebhookSuscripcion> ProcesarAsync(EventoWebhookProveedorDto evento, CancellationToken cancellationToken);
}

/// <summary>
/// Procesa un evento de Subscription ya verificado por firma
/// (<see cref="IPaymentProvider.VerificarYLeerWebhook"/>) y actualiza el estado comercial del
/// Tenant beneficiario de esa Subscription.
///
/// <para>
/// Del evento solo se usa el Id de la Subscription. El estado NO sale de la copia que trae el
/// evento, sino de una consulta a Stripe en el momento de procesarlo
/// (<see cref="IPaymentProvider.ObtenerSuscripcionAsync"/>): Stripe no garantiza el orden de
/// entrega y reintenta durante días, y aplicar la copia dejaba que un
/// <c>customer.subscription.updated</c> atrasado devolviera a un Tenant ya regularizado a «aviso
/// de impago», o reactivara uno cancelado.
/// </para>
///
/// <para>
/// La consulta y el guardado van dentro de una transacción con el candado de la Subscription
/// (<see cref="IBloqueoSuscripcionStripe"/>), que comparten el alta manual y la
/// resincronización. Así dos entregas concurrentes, o una entrega y un alta a medias, no
/// pueden guardar en el orden contrario al que consultaron. Con eso, cualquier evento, en
/// cualquier orden y repetido, deja el estado que Stripe reporta al último procesamiento. Si
/// el estado no cambia no se escribe nada, ni siquiera
/// <c>EstadoComercialActualizadoEnUtc</c>.
/// </para>
///
/// <para>
/// El candado se intenta sin esperar: si otro procesamiento lo tiene (otra entrega de la misma
/// Subscription, o un alta a medias), se pide reintento a Stripe en vez de ocupar una conexión
/// del pool esperando. La consulta a Stripe se acota a <see cref="LimiteConsulta"/>: el
/// candado está tomado mientras dura, y Stripe da por fallida una entrega que tarda demasiado.
/// Un tiempo agotado cuenta como proveedor no disponible, y Stripe reintenta.
/// </para>
/// </summary>
public class ProcesadorWebhookSuscripcion(
    ITenantsQueryContext dbContext,
    IPaymentProvider paymentProvider,
    ITransaccionDeComando transaccion,
    IBloqueoSuscripcionStripe bloqueo,
    IDescarteCambiosPendientes descarte,
    IUnitOfWork unitOfWork) : IProcesadorWebhookSuscripcion
{
    public static readonly TimeSpan TiempoMaximoConsulta = TimeSpan.FromSeconds(10);

    /// <summary><see cref="TiempoMaximoConsulta"/> salvo en los tests, que no esperan diez segundos.</summary>
    public TimeSpan LimiteConsulta { get; init; } = TiempoMaximoConsulta;

    public async Task<ProcesamientoWebhookSuscripcion> ProcesarAsync(
        EventoWebhookProveedorDto evento, CancellationToken cancellationToken)
    {
        if (evento.Suscripcion is not { } suscripcionDelEvento)
            return new(ResultadoWebhookSuscripcion.SinSuscripcion);

        var idSuscripcion = suscripcionDelEvento.IdSuscripcion;
        ProcesamientoWebhookSuscripcion procesamiento = new(ResultadoWebhookSuscripcion.SinSuscripcion);

        // La operación puede ejecutarse más de una vez (estrategia de reintentos de
        // ITransaccionDeComando): todo lo que decide se lee dentro.
        await transaccion.EjecutarAsync(async ct =>
        {
            procesamiento = await ProcesarBajoCandadoAsync(idSuscripcion, ct);
            return Result.Exito();
        }, cancellationToken);

        return procesamiento;
    }

    private async Task<ProcesamientoWebhookSuscripcion> ProcesarBajoCandadoAsync(string idSuscripcion, CancellationToken ct)
    {
        if (!await bloqueo.IntentarBloquearSuscripcionAsync(idSuscripcion, ct))
            return new(ResultadoWebhookSuscripcion.SuscripcionOcupada);

        // Contexto limpio tras el candado: si el Tenant ya estuviera seguido, la consulta
        // devolvería esa copia y no lo que confirmó quien tenía el candado.
        descarte.DescartarCambiosPendientes();

        var tenant = await dbContext.Tenants.SingleOrDefaultAsync(t => t.StripeSubscriptionId == idSuscripcion, ct);
        if (tenant is null)
            return new(ResultadoWebhookSuscripcion.SinTenantVinculado);

        if (!tenant.EsSuscribible())
            return new(ResultadoWebhookSuscripcion.TenantNoSuscribible);

        Result<SuscripcionProveedorDto> actual;
        using (var limite = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            limite.CancelAfter(LimiteConsulta);
            try
            {
                actual = await paymentProvider.ObtenerSuscripcionAsync(idSuscripcion, limite.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new(ResultadoWebhookSuscripcion.ProveedorNoDisponible, "PaymentProvider.TiempoAgotado");
            }
        }

        if (actual.EsFallido)
            return new(ResultadoWebhookSuscripcion.ProveedorNoDisponible, actual.Error.Codigo);

        var nuevoEstado = MapeoEstadoComercialTenant.Desde(actual.Valor.Estado);
        if (nuevoEstado is null)
            return new(ResultadoWebhookSuscripcion.EstadoNoReconocido);

        if (tenant.EstadoComercial == nuevoEstado.Value)
            return new(ResultadoWebhookSuscripcion.SinCambios);

        tenant.ActualizarEstadoComercial(nuevoEstado.Value);
        await unitOfWork.SaveChangesAsync(ct);

        return new(ResultadoWebhookSuscripcion.Aplicado);
    }
}
