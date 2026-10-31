using System.Linq.Expressions;
using CaeManager.Domain.Tenants;

namespace CaeManager.Domain.ApiKeys;

/// <summary>
/// Qué ventana de Soporte TALVEG respalda una clave de la API pública. Son dos criterios
/// distintos, uno para cada momento, y no se intercambian:
/// <list type="bullet">
/// <item><see cref="PuedeEmitir"/> (lo exige <c>GenerarClaveApiCommand</c>): la delegación une la
/// organización de origen de la cuenta emisora con el Tenant propietario, es de propósito Soporte
/// y está activa y vigente en el momento de emitir;</item>
/// <item><see cref="PuedeAutenticar"/> (lo exige <c>ApiKeyAuthenticationHandler</c> en cada
/// petición): además, sigue abierta la misma ventana en que se emitió la clave.</item>
/// </list>
/// <para>
/// La clave queda invalidada al cerrarse esa ventana, a mano o por expiración (decisión del
/// 2026-10-28 concretada el 2026-10-31; OWASP ASVS 4.0.3 V4.1.3 y V4.1.5), así que vive como mucho
/// lo que dura una ventana de <c>AbrirAccesoSoporteCommand</c>, sea cual sea su
/// <see cref="ClaveApi.CaducaEnUtc"/>. Reabrir no la devuelve: la reapertura reutiliza la fila de
/// la delegación y sobrescribe <see cref="DelegacionTenant.ActivadaEnUtc"/>, que entonces es
/// posterior a la emisión.
/// </para>
/// </summary>
public static class CriterioSoporteClaveApi
{
    /// <summary>
    /// Sigue abierta en <paramref name="ahoraUtc"/> la ventana de Soporte desde
    /// <paramref name="tenantOrigenId"/> hacia <paramref name="tenantPropietarioId"/> en que se
    /// emitió la clave. Es una expresión porque la traduce EF; las columnas nulas dan false.
    /// </summary>
    public static Expression<Func<DelegacionTenant, bool>> PuedeAutenticar(
        Guid tenantOrigenId, Guid tenantPropietarioId, DateTime emitidaEnUtc, DateTime ahoraUtc) =>
        d => d.TenantConsultoraId == tenantOrigenId && d.TenantClienteId == tenantPropietarioId
            && d.Proposito == PropositoDelegacion.Soporte
            && d.Activa && ahoraUtc < d.ExpiraEnUtc && d.ActivadaEnUtc <= emitidaEnUtc;

    /// <summary>
    /// La delegación une a las dos organizaciones, es de propósito Soporte y está activa y vigente
    /// en <paramref name="ahoraUtc"/> (<see cref="DelegacionTenant.EstaVigente"/>).
    /// </summary>
    public static bool PuedeEmitir(DelegacionTenant delegacion, Guid tenantOrigenId, Guid tenantPropietarioId, DateTime ahoraUtc) =>
        PuedeAutenticar(tenantOrigenId, tenantPropietarioId, ahoraUtc, ahoraUtc).Compile()(delegacion) && delegacion.EstaVigente(ahoraUtc);
}
