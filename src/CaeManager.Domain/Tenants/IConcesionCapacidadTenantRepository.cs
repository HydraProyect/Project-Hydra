namespace CaeManager.Domain.Tenants;

/// <summary>
/// Concesiones de capacidad del Tenant actual (el filtro global y la RLS lo acotan; nunca se pasa el Tenant como
/// parámetro).
/// </summary>
public interface IConcesionCapacidadTenantRepository
{
    /// <summary>La concesión vigente de esa cuenta y capacidad en el Tenant actual, para modificarla.</summary>
    Task<ConcesionCapacidadTenant?> ObtenerVigenteAsync(Guid usuarioId, CapacidadTenant capacidad, CancellationToken cancellationToken);

    /// <summary>
    /// Si la cuenta tiene hoy una concesión vigente de esa capacidad en el Tenant actual. Lee la base sin
    /// seguimiento: una revocación guardada desde otro circuito se ve en la siguiente decisión.
    /// </summary>
    Task<bool> TieneVigenteAsync(Guid usuarioId, CapacidadTenant capacidad, CancellationToken cancellationToken);

    void Agregar(ConcesionCapacidadTenant concesion);

    /// <summary>
    /// Guarda una concesión nueva. <c>false</c> si otra concesión vigente de la misma cuenta y capacidad se guardó
    /// entremedias (índice único parcial): no queda nada guardado.
    /// </summary>
    Task<bool> GuardarConcesionAsync(CancellationToken cancellationToken);
}
