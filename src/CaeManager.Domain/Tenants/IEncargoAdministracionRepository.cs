using CaeManager.Domain.Operaciones;

namespace CaeManager.Domain.Tenants;

/// <summary>
/// Escritura del catálogo de <see cref="EncargoAdministracion"/>. Toda lectura
/// va acotada por el Tenant propietario: el catálogo está fuera del filtro
/// global de Tenant y no existe ni debe existir un «listar todos».
/// </summary>
public interface IEncargoAdministracionRepository
{
    /// <summary>El encargo, rastreado, si es de ese Tenant propietario.</summary>
    Task<EncargoAdministracion?> ObtenerPorIdAsync(
        Guid id, Guid propietarioTenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// La operación a la que se quiere ligar un encargo, si es de ese Tenant
    /// propietario. Que sea externa, universal y vigente lo exige
    /// <see cref="EncargoAdministracion.Registrar"/>.
    /// </summary>
    Task<AsignacionOperacion?> ObtenerOperacionAsync(
        Guid asignacionOperacionId, Guid propietarioTenantId, CancellationToken cancellationToken = default);

    /// <summary>Si la operación ya tiene un encargo sin retirar (el índice único parcial lo impide igualmente).</summary>
    Task<bool> ExisteSinRetirarAsync(Guid asignacionOperacionId, CancellationToken cancellationToken = default);

    void Agregar(EncargoAdministracion encargo);
}
