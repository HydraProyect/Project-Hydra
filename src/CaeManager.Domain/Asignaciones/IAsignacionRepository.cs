namespace CaeManager.Domain.Asignaciones;

public interface IAsignacionRepository
{
    Task<Asignacion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExisteActivaAsync(Guid trabajadorId, Guid centroId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Si alguna asignación —activa o ya cerrada— del mismo trío (Tenant,
    /// Trabajador, Centro) se solapa con el rango dado. DEC-19: a diferencia
    /// de <see cref="ExisteActivaAsync"/>, esto SÍ mira las cerradas — el
    /// hueco que <c>IX_Asignaciones_TenantId_TrabajadorId_CentroId_Activa</c>
    /// nunca cubrió. Ver <see cref="Asignacion.SeSolapaCon"/> para el límite
    /// exacto del rango.
    /// </summary>
    Task<bool> ExisteSolapeAsync(
        Guid trabajadorId, Guid centroId, DateOnly fechaAlta, DateOnly? fechaBaja, CancellationToken cancellationToken = default);

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
