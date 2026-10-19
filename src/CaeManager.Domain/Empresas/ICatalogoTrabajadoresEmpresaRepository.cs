namespace CaeManager.Domain.Empresas;

/// <summary>
/// Acceso a las Delegaciones y Oficios de una Empresa (<see cref="DelegacionEmpresa"/>, <see cref="OficioEmpresa"/>).
/// Las lecturas pasan por el filtro de Tenant y de borrado lógico del contexto, y debajo por la RLS de las dos tablas.
/// </summary>
public interface ICatalogoTrabajadoresEmpresaRepository
{
    Task<DelegacionEmpresa?> ObtenerDelegacionEmpresaAsync(Guid id, CancellationToken cancellationToken = default);

    Task<OficioEmpresa?> ObtenerOficioAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DelegacionEmpresa>> ListarDelegacionesEmpresaAsync(Guid empresaId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OficioEmpresa>> ListarOficiosAsync(Guid empresaId, CancellationToken cancellationToken = default);

    /// <summary>Trabajadores no eliminados que tienen asignada esta Delegación.</summary>
    Task<int> ContarTrabajadoresDeDelegacionEmpresaAsync(Guid delegacionId, CancellationToken cancellationToken = default);

    /// <summary>Trabajadores no eliminados que tienen asignado este Oficio.</summary>
    Task<int> ContarTrabajadoresDeOficioAsync(Guid oficioId, CancellationToken cancellationToken = default);

    void Agregar(DelegacionEmpresa delegacion);

    void Agregar(OficioEmpresa oficio);

    /// <summary>
    /// Guarda los cambios pendientes. Devuelve <c>false</c> si chocan con el índice único de nombre por Empresa (dos
    /// altas o renombres simultáneos con el mismo nombre); en ese caso deja sin efecto en el contexto el alta o el
    /// cambio que perdió la carrera, para que el siguiente guardado del circuito no lo arrastre.
    /// </summary>
    Task<bool> GuardarCambiosAsync(CancellationToken cancellationToken = default);
}
