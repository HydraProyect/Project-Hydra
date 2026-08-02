namespace CaeManager.Domain.ApiKeys;

public interface IClaveApiRepository
{
    Task<ClaveApi?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Único punto de lectura que ignora el filtro global de tenant (ver
    /// implementación) — el momento en que se autentica una clave es,
    /// estructuralmente, el único en que el tenant todavía no se conoce: es
    /// justo lo que la clave va a resolver. Mismo motivo por el que
    /// AspNetUsers queda fuera del filtro para el login.
    /// </summary>
    Task<ClaveApi?> ObtenerPorHashAsync(string hashClave, CancellationToken cancellationToken = default);

    void Agregar(ClaveApi clave);
}
