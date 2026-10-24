using System.Linq.Expressions;
using CaeManager.Domain.Tenants;

namespace CaeManager.Domain.ApiKeys;

/// <summary>
/// Qué delegación de propósito Soporte respalda una clave de la API pública. Son dos
/// criterios distintos, uno para cada momento, y no se intercambian:
/// <list type="bullet">
/// <item><see cref="PuedeEmitir"/> (lo exige <c>GenerarClaveApiCommand</c>): la delegación
/// une la organización de origen de la cuenta emisora con el Tenant propietario, es de
/// propósito Soporte y además está activa y vigente en el momento de emitir;</item>
/// <item><see cref="PuedeAutenticar"/> (lo exige <c>ApiKeyAuthenticationHandler</c> en cada
/// petición): basta con que esa delegación de Soporte exista entre el mismo origen y el mismo
/// Tenant propietario, sin mirar si está activa ni vigente. Una delegación de Soporte solo
/// está activa durante la ventana de <c>AbrirAccesoSoporteCommand</c> (como mucho unas horas),
/// así que exigir ventana abierta al autenticar dejaría sin servicio a toda clave fuera de ella.
/// La vida de la clave la acotan su revocación y su <see cref="ClaveApi.CaducaEnUtc"/>.</item>
/// </list>
/// <para>
/// Hoy no se distingue una delegación de Soporte retirada a propósito de una con la ventana
/// cerrada: las dos están inactivas y las dos dejan autenticar la clave. Distinguirlas —para que
/// retirar el soporte invalide sus claves— queda como incremento aparte.
/// </para>
/// </summary>
public static class CriterioSoporteClaveApi
{
    /// <summary>
    /// Existe una delegación de Soporte desde <paramref name="tenantOrigenId"/> hacia
    /// <paramref name="tenantPropietarioId"/>. No mira <see cref="DelegacionTenant.Activa"/> ni
    /// <see cref="DelegacionTenant.ExpiraEnUtc"/>. Es una expresión porque la traduce EF.
    /// </summary>
    public static Expression<Func<DelegacionTenant, bool>> PuedeAutenticar(Guid tenantOrigenId, Guid tenantPropietarioId) =>
        d => d.TenantConsultoraId == tenantOrigenId && d.TenantClienteId == tenantPropietarioId
            && d.Proposito == PropositoDelegacion.Soporte;

    /// <summary>
    /// Lo de <see cref="PuedeAutenticar"/> y, además, la delegación está activa y vigente en
    /// <paramref name="ahoraUtc"/> (<see cref="DelegacionTenant.EstaVigente"/>).
    /// </summary>
    public static bool PuedeEmitir(DelegacionTenant delegacion, Guid tenantOrigenId, Guid tenantPropietarioId, DateTime ahoraUtc) =>
        PuedeAutenticar(tenantOrigenId, tenantPropietarioId).Compile()(delegacion) && delegacion.EstaVigente(ahoraUtc);
}
