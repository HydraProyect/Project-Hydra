namespace CaeManager.Application.Plataforma;

/// <summary>
/// Abre y cierra el <see cref="AmbitoEscrituraPrivilegiada"/> alrededor de un
/// handler de aprovisionamiento, y con él, el rol de escritura de PostgreSQL
/// que la conexión adopta mientras dure.
///
/// Solo <c>ElevacionEscrituraAprovisionamientoBehavior</c> lo invoca, y solo
/// después de que <c>AutorizacionEscrituraBehavior</c> ya revalidó la sesión
/// en ese mismo comando — la elevación nunca decide por su cuenta si algo se
/// autoriza, solo ejecuta una decisión ya tomada.
///
/// <c>TryAddScoped</c> con <see cref="ElevacionEscrituraPrivilegiadaInerte"/>
/// como valor por defecto (mismo patrón que <see cref="ISesionPrivilegiadaActual"/>):
/// la implementación real, que además mueve el rol de PostgreSQL, vive en
/// Infrastructure y la sustituye en la aplicación de verdad.
/// </summary>
public interface IElevacionEscrituraPrivilegiada
{
    /// <summary>
    /// Abre el ámbito con la sesión YA REVALIDADA (no el token) y, si la
    /// conexión de este ámbito de DI ya está abierta, adopta de inmediato el
    /// rol de escritura acotada. Disponer el resultado cierra el ámbito y, si
    /// la conexión sigue abierta, devuelve el rol a <c>cae_app_soporte</c> —
    /// nunca <c>RESET ROLE</c>, que volvería al rol de login con escritura
    /// completa.
    /// </summary>
    Task<IAsyncDisposable> EstablecerAsync(
        Guid sesionId, Guid tenantObjetivoId, CancellationToken cancellationToken = default);
}
