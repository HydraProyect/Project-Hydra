using CaeManager.Domain.Tenants;

namespace CaeManager.Application.Comercial.Common;

/// <summary>
/// Traduce el estado de una Subscription del proveedor de pago
/// (<see cref="EstadoSuscripcionProveedor"/>) al estado comercial propio de
/// Hydra (<see cref="EstadoComercialTenant"/>) — el único sitio del código
/// que conoce esta correspondencia, para que <c>RegistrarSuscripcionTenantCommand</c>
/// (alta manual) y el endpoint de webhook de Stripe (reacción a cambios)
/// nunca puedan decidir el mapeo cada uno a su manera.
///
/// Función pura y estática a propósito: no depende de nada del tenant
/// concreto (no hay "aviso tras el segundo impago" ni contadores locales —
/// Stripe ya hace ese seguimiento con Smart Retries/dunning y lo comunica en
/// el propio estado de la Subscription, ver docs de Stripe sobre
/// <c>subscription.status</c>). Eso es lo que hace testeable esta pieza sin
/// nada de infraestructura.
/// </summary>
public static class MapeoEstadoComercialTenant
{
    /// <summary>
    /// Null para <see cref="EstadoSuscripcionProveedor.Desconocida"/> a
    /// propósito: un estado de Stripe que Hydra no reconoce (SDK más nuevo
    /// que el código, o valor inesperado) no debe traducirse a ciegas a
    /// ninguna de las cinco etapas del gate — "no tocar el estado actual y
    /// registrar un aviso" es la respuesta segura aquí, nunca "asumir
    /// impago" ni "asumir que sigue activa".
    /// </summary>
    public static EstadoComercialTenant? Desde(EstadoSuscripcionProveedor estadoProveedor) => estadoProveedor switch
    {
        EstadoSuscripcionProveedor.Activa => EstadoComercialTenant.Activa,
        EstadoSuscripcionProveedor.EnPeriodoGracia => EstadoComercialTenant.AvisoImpago,
        EstadoSuscripcionProveedor.Impagada => EstadoComercialTenant.SoloLectura,
        EstadoSuscripcionProveedor.Cancelada => EstadoComercialTenant.Suspendida,
        _ => null,
    };
}
