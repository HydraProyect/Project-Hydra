using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comercial.Common;

/// <summary>Qué hizo <see cref="ProcesadorWebhookSuscripcion"/> con un evento ya verificado.</summary>
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

    /// <summary>Estado comercial actualizado con el estado actual de la Subscription.</summary>
    Aplicado,
}

/// <summary>
/// Procesa un evento de Subscription ya verificado por firma
/// (<see cref="IPaymentProvider.VerificarYLeerWebhook"/>) y actualiza el estado comercial del
/// Tenant beneficiario de esa Subscription.
///
/// El estado NO se toma de la copia de la Subscription que trae el evento, sino de una
/// consulta a Stripe en el momento de procesarlo (<see cref="IPaymentProvider.ObtenerSuscripcionAsync"/>).
/// Stripe no garantiza el orden de entrega de los eventos y reintenta durante días los que
/// fallaron: aplicar la copia del evento dejaría que un <c>customer.subscription.updated</c>
/// atrasado (p. ej. <c>past_due</c>, entregado después del que volvió a <c>active</c>) devolviera
/// a un Tenant ya regularizado a «aviso de impago», o a uno ya cancelado a «activa». Volver a
/// consultar hace que cualquier evento, en cualquier orden y repetido cuantas veces sea, deje el
/// mismo estado: el último que conoce Stripe. Es también lo que hace idempotente el endpoint sin
/// guardar los Id de evento ya procesados.
///
/// El Tenant se busca ANTES de consultar a Stripe: una Subscription sin Tenant vinculado no
/// gasta una llamada a la API.
/// </summary>
public class ProcesadorWebhookSuscripcion(
    ITenantsQueryContext dbContext,
    IPaymentProvider paymentProvider,
    IUnitOfWork unitOfWork)
{
    public async Task<ResultadoWebhookSuscripcion> ProcesarAsync(
        EventoWebhookProveedorDto evento, CancellationToken cancellationToken)
    {
        if (evento.Suscripcion is not { } suscripcionDelEvento)
            return ResultadoWebhookSuscripcion.SinSuscripcion;

        var tenant = await dbContext.Tenants
            .SingleOrDefaultAsync(t => t.StripeSubscriptionId == suscripcionDelEvento.IdSuscripcion, cancellationToken);

        if (tenant is null)
            return ResultadoWebhookSuscripcion.SinTenantVinculado;

        if (!tenant.EsSuscribible())
            return ResultadoWebhookSuscripcion.TenantNoSuscribible;

        var actual = await paymentProvider.ObtenerSuscripcionAsync(suscripcionDelEvento.IdSuscripcion, cancellationToken);
        if (actual.EsFallido)
            return ResultadoWebhookSuscripcion.ProveedorNoDisponible;

        var nuevoEstado = MapeoEstadoComercialTenant.Desde(actual.Valor.Estado);
        if (nuevoEstado is null)
            return ResultadoWebhookSuscripcion.EstadoNoReconocido;

        tenant.ActualizarEstadoComercial(nuevoEstado.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return ResultadoWebhookSuscripcion.Aplicado;
    }
}
