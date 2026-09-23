namespace CaeManager.Domain.Operaciones;

/// <summary>
/// Acceso a las <see cref="SolicitudIncorporacionCartera"/>. La base de datos
/// ya acota las filas al Operador CAE de quien consulta (política RLS por
/// <c>app.tenant_origen_id</c>); los filtros por operador de estos métodos
/// no la sustituyen, la repiten para que un fallo de una de las dos capas no
/// baste para ver solicitudes de otro Operador CAE.
/// </summary>
public interface ISolicitudIncorporacionCarteraRepository
{
    Task<SolicitudIncorporacionCartera?> ObtenerPorIdAsync(
        Guid id, Guid operadorTenantId, CancellationToken cancellationToken = default);

    /// <summary>Las pendientes del Operador CAE, las más antiguas primero: son las que llevan más tiempo esperando.</summary>
    Task<IReadOnlyList<SolicitudIncorporacionCartera>> ListarPendientesAsync(
        Guid operadorTenantId, CancellationToken cancellationToken = default);

    /// <summary>Las aceptadas del Operador CAE que todavía no se han revocado.</summary>
    Task<IReadOnlyList<SolicitudIncorporacionCartera>> ListarAceptadasAsync(
        Guid operadorTenantId, CancellationToken cancellationToken = default);

    /// <summary>Todas las de un solicitante, las más recientes primero.</summary>
    Task<IReadOnlyList<SolicitudIncorporacionCartera>> ListarDelSolicitanteAsync(
        Guid operadorTenantId, Guid solicitanteUsuarioId, CancellationToken cancellationToken = default);

    Task<bool> ExistePendienteAsync(
        Guid asignacionOperacionId, Guid solicitanteUsuarioId, CancellationToken cancellationToken = default);

    void Agregar(SolicitudIncorporacionCartera solicitud);
}
