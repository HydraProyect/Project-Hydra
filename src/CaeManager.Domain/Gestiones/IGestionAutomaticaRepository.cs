namespace CaeManager.Domain.Gestiones;

public interface IGestionAutomaticaRepository
{
    /// <summary>La Gestión automática del Documento, con sus pasos; null si no nació ninguna.</summary>
    Task<GestionAutomatica?> ObtenerPorDocumentoIdAsync(Guid documentoId, CancellationToken cancellationToken = default);

    void Agregar(GestionAutomatica gestion);
}
