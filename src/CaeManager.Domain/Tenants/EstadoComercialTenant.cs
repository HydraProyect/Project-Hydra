namespace CaeManager.Domain.Tenants;

/// <summary>
/// Estado de la suscripción de pago de un Tenant (Horizonte 1.7 del plan de
/// negocio, "Billing mínimo viable") — deliberadamente separado de
/// <see cref="EstadoTenant"/>, que modela otra cosa por completo: si un
/// Operador Delegado sigue teniendo acceso de soporte concedido por un
/// Administrador de plataforma. Confundir ambos convertiría dos motivos de
/// restricción sin relación (un admin revocando delegación vs. Stripe
/// avisando de un impago) en el mismo interruptor — exactamente el
/// acoplamiento accidental que este campo evita.
///
/// <see cref="SinSuscripcion"/> es el valor por defecto y deliberadamente NO
/// restringe nada (ver <c>GateComercialTenantBehavior</c>): todo tenant
/// existente antes de este Horizonte, y el propio tenant de plataforma, se
/// despliega en este estado y sigue operando exactamente igual que hoy. El
/// gate solo empieza a aplicar una vez que un tenant tiene una suscripción de
/// Stripe vinculada — no hay "big bang" de bloqueo al desplegar esta feature.
///
/// Las etapas a partir de <see cref="Activa"/> siguen literalmente el gate
/// descrito en el plan de negocio § 1.7: "aviso → solo lectura →
/// suspendido". Impulsadas por el estado real de la Subscription de Stripe
/// (ver <c>MapeoEstadoComercialTenant</c>), nunca inferidas localmente contando
/// reintentos — Stripe ya hace ese seguimiento (dunning/Smart Retries) y
/// notifica el resultado por webhook.
/// </summary>
public enum EstadoComercialTenant
{
    SinSuscripcion,
    Activa,
    AvisoImpago,
    SoloLectura,
    Suspendida,
}
