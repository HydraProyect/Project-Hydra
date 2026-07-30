namespace CaeManager.Domain.Comunicaciones;

public interface IMacroRespuestaRepository
{
    Task<MacroRespuesta?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(MacroRespuesta macro);
}
