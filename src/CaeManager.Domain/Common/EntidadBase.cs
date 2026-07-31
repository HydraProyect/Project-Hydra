namespace CaeManager.Domain.Common;

/// <summary>
/// Entidad con ciclo de vida de negocio: soft delete y timestamp de creación.
/// La auditoría detallada (quién/qué cambió) la captura el interceptor de
/// Infrastructure, no esta clase. Extiende <see cref="EntidadConTenant"/> —
/// todo agregado raíz de negocio pertenece a un tenant (ver
/// docs/MULTITENANCY.md).
/// </summary>
public abstract class EntidadBase : EntidadConTenant
{
    /// <summary>
    /// Token de concurrencia optimista. Dos usuarios editando el mismo
    /// registro se pisaban en silencio: el último en guardar ganaba y el
    /// primero no se enteraba de que su cambio había desaparecido — en un
    /// producto donde varios gestores CAE trabajan sobre la misma cartera, y
    /// donde un Documento mal sobrescrito es una vigencia mal calculada.
    ///
    /// Un <see cref="Guid"/> y no un <c>rowversion</c>/<c>xmin</c> nativo
    /// porque tiene que funcionar igual en SQLite hoy y en PostgreSQL después
    /// (condición de salida de ADR-003) — el valor lo renueva
    /// <c>ConcurrenciaOptimistaInterceptor</c> en cada modificación, no la
    /// base de datos.
    ///
    /// <c>private set</c> por el mismo motivo que <c>TenantId</c>: ningún
    /// Command lo toca.
    /// </summary>
    public Guid Version { get; private set; } = Guid.NewGuid();

    public DateTime CreadoEnUtc { get; protected set; } = DateTime.UtcNow;
    public bool EstaEliminado { get; private set; }
    public DateTime? EliminadoEnUtc { get; private set; }
    public Guid? EliminadoPorUsuarioId { get; private set; }

    public void MarcarComoEliminado(Guid usuarioId)
    {
        if (EstaEliminado) return;
        EstaEliminado = true;
        EliminadoEnUtc = DateTime.UtcNow;
        EliminadoPorUsuarioId = usuarioId;
    }

    public void Restaurar()
    {
        EstaEliminado = false;
        EliminadoEnUtc = null;
        EliminadoPorUsuarioId = null;
    }
}
