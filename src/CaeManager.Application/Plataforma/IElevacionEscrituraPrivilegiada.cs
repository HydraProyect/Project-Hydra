namespace CaeManager.Application.Plataforma;

/// <summary>
/// Mueve el rol de escritura de PostgreSQL de la conexión de este ámbito de
/// DI mientras dura un <see cref="AmbitoEscrituraPrivilegiada"/> ya abierto.
///
/// Solo <c>ElevacionEscrituraAprovisionamientoBehavior</c> lo invoca, y solo
/// después de que <c>AutorizacionEscrituraBehavior</c> ya revalidó la sesión
/// en ese mismo comando — esta pieza nunca decide por su cuenta si algo se
/// autoriza, solo ejecuta una decisión ya tomada.
///
/// <b>Deliberadamente NO abre el <see cref="AmbitoEscrituraPrivilegiada"/>.</b>
/// La revisión 5 (Codex, verificación de composición completa) encontró que
/// hacerlo desde dentro de un método <c>async</c> de esta interfaz perdía la
/// mutación del <c>AsyncLocal</c> en el llamador cuando el método completaba
/// de forma síncrona (la conexión ya estaba cerrada, el caso más común): el
/// <c>AsyncTaskMethodBuilder</c> ejecuta ese tramo síncrono bajo una copia del
/// <see cref="System.Threading.ExecutionContext"/> que no se propaga de vuelta
/// al llamador si el método nunca llega a suspenderse de verdad. El síntoma
/// era exacto: <c>AmbitoEscrituraPrivilegiada.Establecer</c> dejaba el ámbito
/// visible DENTRO del método que lo abría, pero
/// <c>ElevacionEscrituraAprovisionamientoBehavior</c> —su llamador— veía
/// <c>Actual</c> vacío justo después de esperar la llamada, y la conexión
/// siguiente adoptaba <c>cae_app_soporte</c> en vez de
/// <c>cae_app_aprovisionamiento</c> — la importación bajo Aprovisionamiento
/// fallaba siempre con 42501. Por eso <c>AmbitoEscrituraPrivilegiada.Establecer</c>
/// se llama ahora directamente en el behavior (código síncrono, sin cruzar la
/// frontera de un método <c>async</c> ajeno) y esta interfaz solo mueve el rol
/// de Postgres, que sí puede vivir en un método <c>async</c> porque nada aquí
/// depende de que su mutación sobreviva de vuelta en el llamador.
/// </summary>
public interface IElevacionEscrituraPrivilegiada
{
    /// <summary>
    /// Si la conexión de este ámbito de DI ya está abierta, adopta de
    /// inmediato el rol de escritura acotada. Si no lo está, no hace nada: la
    /// próxima apertura la decide <c>TenantRlsConnectionInterceptor</c>
    /// consultando el <see cref="AmbitoEscrituraPrivilegiada"/> que el
    /// llamador ya tiene que haber abierto antes de invocar esto.
    /// </summary>
    Task ElevarSiConexionAbiertaAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Si la conexión sigue abierta, devuelve el rol a <c>cae_app_soporte</c>
    /// — nunca <c>RESET ROLE</c>, que volvería al rol de login con escritura
    /// completa.
    /// </summary>
    Task DevolverSiConexionAbiertaAsync(CancellationToken cancellationToken = default);
}
