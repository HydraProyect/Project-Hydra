namespace CaeManager.Domain.Common;

/// <summary>
/// Entidad con ciclo de vida de negocio: soft delete y timestamp de creación.
/// La auditoría detallada (quién/qué cambió) la captura el interceptor de
/// Infrastructure, no esta clase.
/// </summary>
public abstract class EntidadBase : Entity
{
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
