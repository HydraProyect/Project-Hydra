namespace CaeManager.Domain.Tenants;

public interface ITenantRepository
{
    Task<bool> ExisteConNombreAsync(string nombre, CancellationToken cancellationToken = default);

    /// <summary>El Tenant rastreado, para mutarlo y guardarlo con la unidad de trabajo.</summary>
    Task<Tenant?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(Tenant tenant);
}
