namespace CaeManager.Infrastructure.Coordinacion;

/// <summary>
/// Elección de líder entre réplicas para un <c>BackgroundService</c> que no
/// debe correr a la vez en más de un proceso — hoy
/// <c>ProcesadorAnalisisDocumentoHostedService</c> (dos réplicas leyendo
/// "el siguiente trabajo pendiente" sin bloqueo de fila competirían por el
/// mismo <c>TrabajoAnalisisDocumento</c> y podrían procesarlo dos veces).
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
