namespace CaeManager.Domain.Tenants;

public interface ITenantRepository
{
    Task<bool> ExisteConNombreAsync(string nombre, CancellationToken cancellationToken = default);

    void Agregar(Tenant tenant);
}
