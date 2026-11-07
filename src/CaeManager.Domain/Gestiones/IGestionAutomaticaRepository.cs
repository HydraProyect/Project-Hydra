namespace CaeManager.Domain.Gestiones;

public interface IGestionAutomaticaRepository
{
    /// <summary>La Gestión automática del Documento, con sus pasos; null si no nació ninguna.</summary>
    Task<GestionAutomatica?> ObtenerPorDocumentoIdAsync(Guid documentoId, CancellationToken cancellationToken = default);

    Task<bool> ExisteParaDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default);

    void Agregar(GestionAutomatica gestion);

    /// <summary>
    /// Deja de seguir estas Gestiones y sus pasos sin guardarlos. Lo usa quien traga un fallo después de haberlas
    /// añadido o modificado, para que el siguiente guardado del mismo contexto (otro comando de la misma sesión) no
    /// reintente una escritura que ya falló.
    /// </summary>
    void Descartar(IEnumerable<GestionAutomatica> gestiones);
}
