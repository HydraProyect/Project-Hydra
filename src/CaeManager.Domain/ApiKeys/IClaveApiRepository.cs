namespace CaeManager.Domain.ApiKeys;

public interface IClaveApiRepository
{
    Task<ClaveApi?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// De qué Tenant es la clave cuyo hash se recibe, o <c>null</c> si no hay
    /// ninguna (o está eliminada). Existe porque autenticar una clave es,
    /// estructuralmente, el único momento en que el tenant todavía no se
    /// conoce: es justo lo que la clave va a resolver. Mismo motivo por el que
    /// AspNetUsers queda fuera del filtro para el login.
    ///
    /// <para>
    /// Devuelve el Tenant y nada más, a propósito: con él, el llamador entra en
    /// <c>AmbitoTenantExplicito</c> y lee la fila entera por la vía normal
    /// (<see cref="ObtenerPorHashAsync"/>), con el filtro global de EF y la
    /// política RLS aplicándose. Ver la implementación y la migración
    /// 20260921155801_ResolucionDeClaveApiBajoRls.
    /// </para>
    /// </summary>
    Task<Guid?> ObtenerTenantPorHashAsync(string hashClave, CancellationToken cancellationToken = default);

    /// <summary>
    /// La clave con ese hash <b>dentro del tenant activo</b>. Solo devuelve
    /// algo si el llamador ya estableció el tenant que
    /// <see cref="ObtenerTenantPorHashAsync"/> le dio: sin él, ni el filtro
    /// global ni RLS encuentran la fila.
    /// </summary>
    Task<ClaveApi?> ObtenerPorHashAsync(string hashClave, CancellationToken cancellationToken = default);

    void Agregar(ClaveApi clave);
}
