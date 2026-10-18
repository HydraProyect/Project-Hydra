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
}
