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

    /// <summary>
    /// ¿La clave sigue respaldada, todo a la vez? El Tenant propietario la admite
    /// (<c>Tenant.AdmiteClaveEmitidaEn</c>), la cuenta <paramref name="creadaPorUsuarioId"/> la
    /// respalda (existe, no está desactivada y no se reactivó después de emitirla; un bloqueo
    /// temporal por intentos fallidos no cuenta) y sigue abierta la ventana de Soporte en que se
    /// emitió (<see cref="CriterioSoporteClaveApi.PuedeAutenticar"/>). No mira revocación ni
    /// caducidad, que son de la propia clave. Una sola interpretación para autenticar y para los
    /// listados. El origen se resuelve por la cuenta, no por la sesión: al autenticar por clave no
    /// hay persona. La cuenta es de otra organización, así que no se puede leer su fila bajo RLS.
    /// Se llama dentro del <c>AmbitoTenantExplicito</c> del Tenant propietario.
    /// </summary>
    Task<bool> ClaveSigueRespaldadaAsync(Guid tenantPropietarioId, Guid creadaPorUsuarioId, DateTime emitidaEnUtc, CancellationToken cancellationToken = default);

    void Agregar(ClaveApi clave);
}
