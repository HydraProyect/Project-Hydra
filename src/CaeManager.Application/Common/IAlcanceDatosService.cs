namespace CaeManager.Application.Common;

/// <summary>
/// Resuelve qué Clientes/Centros/Empresas/Subcontratas/Trabajadores/
/// Vehículos puede ver el usuario actual, según su rol (ver Roles.cs en
/// CaeManager.Infrastructure.Identity — Application no puede referenciarlo
/// directamente, así que la implementación real vive en Infrastructure).
///
/// Cada método devuelve null cuando el rol no tiene restricción
/// (Administrador, DireccionCae, Consulta ven todo) — los handlers de
/// Query interpretan null como "no añadir ningún filtro". Un rol
/// restringido (GestorCae, CoordinadorCae, Cliente) sin ningún Cliente
/// visible todavía devuelve una lista VACÍA, nunca null, para que un
/// filtro `Contains` sobre una lista vacía no deje pasar nada por accidente.
///
/// Solo se aplica a las consultas de LISTADO (tablas, Dashboard, Alertas,
/// Reportes) — los selectores de "elige de la base general" (Trabajador,
/// Vehículo: ver ObtenerTrabajadoresParaSelectorQuery/
/// ObtenerVehiculosParaSelectorQuery) se dejan sin restringir a propósito,
/// porque un mismo Trabajador de una Subcontrata puede prestar servicio a
/// varios Clientes de distintos Gestores CAE, y hace falta poder añadirlo
/// a los propios Centros aunque todavía no aparezca en el listado visible.
/// </summary>
public interface IAlcanceDatosService
{
    /// <summary>True para Administrador/DireccionCae/Consulta — sin restricción de cartera.</summary>
    Task<bool> TieneAccesoTotalAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>?> ObtenerClienteIdsVisiblesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>?> ObtenerCentroIdsVisiblesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsVisiblesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsVisiblesAsync(CancellationToken cancellationToken = default);

    /// <summary>Trabajadores con al menos una Asignación activa a un Centro visible — usar solo en listados, no en selectores.</summary>
    Task<IReadOnlyList<Guid>?> ObtenerTrabajadorIdsVisiblesAsync(CancellationToken cancellationToken = default);

    /// <summary>Vehículos de una Empresa/Subcontrata visible — usar solo en listados, no en selectores.</summary>
    Task<IReadOnlyList<Guid>?> ObtenerVehiculoIdsVisiblesAsync(CancellationToken cancellationToken = default);
}
