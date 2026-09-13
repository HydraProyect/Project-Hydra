namespace CaeManager.Infrastructure.Coordinacion;

/// <summary>
/// Cerrojo no bloqueante por clave: como mucho una ejecución de
/// <paramref name="trabajo"/> para la misma <paramref name="clave"/> corre a
/// la vez, sin importar cuántas réplicas o peticiones lo intenten al mismo
/// tiempo. Dos usos hoy, con la misma necesidad de fondo — un componente que
/// no puede ver por sí solo una ejecución concurrente de sí mismo:
///
/// <list type="bullet">
/// <item>Elección de líder entre réplicas para un <c>BackgroundService</c>
/// que no debe correr a la vez en más de un proceso —
/// <c>ProcesadorAnalisisDocumentoHostedService</c> (dos réplicas leyendo "el
/// siguiente trabajo pendiente" sin bloqueo de fila competirían por el mismo
/// <c>TrabajoAnalisisDocumento</c> y podrían procesarlo dos veces).</item>
/// <item><c>LoginCon2fa.razor</c>: página SSR estática (sin
/// <c>@rendermode</c>), donde cada POST reconstruye la instancia del
/// componente — ninguna bandera de instancia puede ver una segunda petición
/// concurrente del mismo usuario, así que dos envíos del mismo código
/// gastarían dos intentos del contador de bloqueo de Identity por una sola
/// acción. El segundo se rechaza aquí, antes de tocar
/// <c>SignInManager</c>.</item>
/// </list>
///
/// Ver <see cref="EleccionLiderPostgresService"/> para la implementación
/// (advisory lock de PostgreSQL) y P3-30 de docs/business/MATURITY_REVIEW.md.
/// </summary>
public interface IEleccionLiderService
{
    /// <summary>
    /// Si esta réplica consigue el liderazgo para <paramref name="clave"/>,
    /// ejecuta <paramref name="trabajo"/> y devuelve <c>true</c>. Si otra
    /// réplica ya lo tiene, no ejecuta nada y devuelve <c>false</c> de
    /// inmediato — nunca espera a que la otra termine.
    /// </summary>
    Task<bool> IntentarEjecutarComoLiderAsync(
        string clave, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken);
}
