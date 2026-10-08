namespace CaeManager.Domain.Operaciones;

/// <summary>
/// Acceso a las <see cref="PropuestaApoyoCartera"/>. La base de datos ya acota las filas al
/// Operador CAE de quien consulta (política RLS por <c>app.tenant_origen_id</c>); los filtros
/// por Operador CAE de estos métodos no la sustituyen, la repiten para que un fallo de una de
/// las dos capas no baste para ver propuestas de otro Operador CAE.
/// </summary>
public interface IPropuestaApoyoCarteraRepository
{
    /// <summary>La propuesta, seguida por el contexto: la carga un Command para cambiarla y su versión detecta la carrera.</summary>
    Task<PropuestaApoyoCartera?> ObtenerPorIdAsync(
        Guid id, Guid operadorTenantId, CancellationToken cancellationToken = default);

    /// <summary>Las pendientes dirigidas a <paramref name="destinatarioUsuarioId"/>, las más antiguas primero. Sin seguimiento.</summary>
    Task<IReadOnlyList<PropuestaApoyoCartera>> ListarPendientesDelDestinatarioAsync(
        Guid operadorTenantId, Guid destinatarioUsuarioId, CancellationToken cancellationToken = default);

    /// <summary>Las pendientes que propuso <paramref name="proponenteUsuarioId"/>, las más antiguas primero. Sin seguimiento.</summary>
    Task<IReadOnlyList<PropuestaApoyoCartera>> ListarPendientesDelProponenteAsync(
        Guid operadorTenantId, Guid proponenteUsuarioId, CancellationToken cancellationToken = default);

    Task<bool> ExistePendienteAsync(
        Guid asignacionOperacionId, Guid destinatarioUsuarioId, CancellationToken cancellationToken = default);

    void Agregar(PropuestaApoyoCartera propuesta);
}
