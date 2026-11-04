namespace CaeManager.Application.Comercial.Common;

/// <summary>
/// Serializa todo lo que lee el estado de una Subscription de Stripe y lo escribe en un
/// Tenant beneficiario: el webhook (<see cref="ProcesadorWebhookSuscripcion"/>), el alta
/// manual (<c>RegistrarSuscripcionTenantCommand</c>) y la resincronización
/// (<c>ActualizarEstadoComercialTenantCommand</c>). Sin esto, en READ COMMITTED, dos de
/// ellos podían consultar Stripe en un orden y guardar en el contrario: el último en
/// guardar, con una lectura ya vieja, ganaba (revisión Codex del D1, 2026-11-04).
///
/// <para>
/// Dos claves. La del Tenant la toman solo el alta y la resincronización, ANTES de leer el
/// Tenant: mientras la tienen, nadie más cambia qué Subscription tiene vinculada. La de la
/// Subscription la toman los tres, y el webhook solo esa, porque al llegar no sabe todavía
/// de qué Tenant es. El alta toma la de la Subscription nueva y la de la que sustituye. Así,
/// un webhook de la vieja no escribe sobre un Tenant ya re-vinculado, y uno de la nueva que
/// llegue mientras el alta está a medias espera a que confirme y encuentra el vínculo. Las de
/// Subscription se toman en orden de clave y siempre después de la de Tenant: no hay ciclo.
/// </para>
///
/// <para>
/// Duran lo que la transacción que las toma (<c>pg_advisory_xact_lock</c>), así que
/// <b>solo valen dentro de una</b> (<see cref="Application.Common.ITransaccionDeComando"/>):
/// fuera de ella la implementación lanza.
/// </para>
/// </summary>
public interface IBloqueoSuscripcionStripe
{
    Task BloquearTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task BloquearSuscripcionesAsync(IReadOnlyCollection<string> idsSuscripcion, CancellationToken cancellationToken = default);
}
