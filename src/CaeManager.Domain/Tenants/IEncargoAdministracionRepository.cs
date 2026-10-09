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

    /// <summary>
    /// Las operaciones de ese Tenant propietario sobre las que hoy se puede
    /// registrar un encargo: externas, del Tenant entero, vigentes en
    /// <paramref name="ahora"/> y sin un encargo sin retirar. Sin seguimiento.
    /// </summary>
    Task<IReadOnlyList<AsignacionOperacion>> ListarOperacionesEncargablesAsync(
        Guid propietarioTenantId, DateTime ahora, CancellationToken cancellationToken = default);

    /// <summary>Si la operación ya tiene un encargo sin retirar (el índice único parcial lo impide igualmente).</summary>
    Task<bool> ExisteSinRetirarAsync(Guid asignacionOperacionId, CancellationToken cancellationToken = default);

    void Agregar(EncargoAdministracion encargo);

    /// <summary>
    /// Guarda y devuelve <c>false</c> si otra escritura registró antes un encargo sin
    /// retirar sobre la misma operación (el índice único parcial) o cambió la fila que
    /// se estaba retirando. En ese caso descarta lo pendiente del contexto.
    /// </summary>
    Task<bool> GuardarDetectandoCarreraAsync(CancellationToken cancellationToken = default);
}
