namespace CaeManager.Domain.Tenants;

public interface IDelegacionTenantRepository
{
    Task<DelegacionTenant?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bloquea la fila de la delegación (<c>FOR UPDATE</c>) hasta el final de la transacción en curso
    /// y, si el contexto ya la rastreaba, relee esa instancia con el estado que la base tiene después
    /// de conseguir el bloqueo: lo que <see cref="ObtenerPorIdAsync"/> devuelva después ya no es lo
    /// leído antes. Así, dos comandos que deciden por su estado (<see cref="DelegacionTenant.Activa"/>)
    /// sobre la misma delegación se ponen en fila: el segundo espera a que el primero confirme y
    /// decide con lo que este dejó escrito. Exige una transacción abierta; devuelve <c>false</c> si
    /// no consiguió la fila bloqueada, y quien llama falla cerrado.
    /// </summary>
    Task<bool> BloquearYReleerAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ExisteActivaAsync(Guid tenantConsultoraId, Guid tenantClienteId, CancellationToken cancellationToken = default);

    void Agregar(DelegacionTenant delegacion);
}
