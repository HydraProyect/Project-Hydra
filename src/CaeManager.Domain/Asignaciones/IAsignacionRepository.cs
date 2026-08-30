namespace CaeManager.Domain.Asignaciones;

public interface IAsignacionRepository
{
    Task<Asignacion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExisteActivaAsync(Guid trabajadorId, Guid centroId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asignaciones activas de un centro, para cerrarlas cuando el centro se
    /// elimina. Devuelve entidades rastreadas a propósito: el cierre pasa por
    /// el dominio y por el interceptor de auditoría, no por un
    /// <c>ExecuteUpdate</c> que los saltaría a los dos.
    /// </summary>
    Task<IReadOnlyList<Asignacion>> ObtenerActivasPorCentroAsync(Guid centroId, CancellationToken cancellationToken = default);

    /// <summary>Asignaciones activas de un trabajador. Ver <see cref="ObtenerActivasPorCentroAsync"/>.</summary>
    Task<IReadOnlyList<Asignacion>> ObtenerActivasPorTrabajadorAsync(Guid trabajadorId, CancellationToken cancellationToken = default);

    void Agregar(Asignacion asignacion);
}
