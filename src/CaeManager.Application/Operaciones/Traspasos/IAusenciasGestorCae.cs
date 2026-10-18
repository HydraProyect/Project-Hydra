using CaeManager.Domain.Operaciones;

namespace CaeManager.Application.Operaciones.Traspasos;

public interface IAusenciasGestorCae
{
    Task<IReadOnlyList<AusenciaGestorCae>> ListarAbiertasAsync(CancellationToken ct);
    Task<AusenciaGestorCae?> ObtenerAsync(Guid id, CancellationToken ct);
    void Agregar(AusenciaGestorCae ausencia);
}
