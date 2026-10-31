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
    /// ¿Existe en el Tenant propietario de la clave una delegación que cumpla
    /// <see cref="CriterioSoporteClaveApi.PuedeAutenticar"/> desde la organización de origen de
    /// la cuenta <paramref name="creadaPorUsuarioId"/>? Solo existencia: no exige que esté activa
    /// ni vigente (eso lo exige la emisión, con <see cref="CriterioSoporteClaveApi.PuedeEmitir"/>).
    /// Devuelve false si la cuenta emisora ya no existe. El origen se resuelve por la cuenta, no
    /// por la sesión: en la autenticación por clave no hay persona ni Tenant de sesión. Se llama
    /// dentro del <c>AmbitoTenantExplicito</c> del Tenant propietario.
    /// </summary>
    Task<bool> ExisteSoporteDelOrigenEmisorAsync(Guid tenantPropietarioId, Guid creadaPorUsuarioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// ¿La cuenta <paramref name="creadaPorUsuarioId"/> sigue respaldando una clave emitida en
    /// <paramref name="emitidaEnUtc"/>? False si la cuenta ya no existe, si está desactivada o si
    /// se desactivó después de emitirla, aunque hoy esté reactivada; un bloqueo temporal por
    /// intentos fallidos no cuenta. La cuenta es de otra organización que el Tenant propietario
    /// de la clave, así que esta comprobación no puede leer su fila bajo RLS.
    /// </summary>
    Task<bool> CuentaEmisoraRespaldaClaveAsync(Guid creadaPorUsuarioId, DateTime emitidaEnUtc, CancellationToken cancellationToken = default);

    void Agregar(ClaveApi clave);
}
