using CaeManager.Application.Common;

namespace CaeManager.Application.Tests;

/// <summary>
/// Fake de <see cref="IDirectorioUsuariosService"/>. Por defecto acepta
/// cualquier usuario, para que los tests que no van de eso no tengan que
/// configurarlo; los que sí lo comprueban pasan <c>esVisible: false</c>.
/// </summary>
public class DirectorioUsuariosServiceFalso(bool esVisible = true) : IDirectorioUsuariosService
{
    public Task<bool> EsVisibleEnTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult(esVisible);

    /// <summary>Mismo criterio que arriba: si el fake acepta a cualquiera, resuelve un nombre sintético; si no, no resuelve ninguno.</summary>
    public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(
        IReadOnlyCollection<Guid> usuarioIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            esVisible
                ? usuarioIds.ToDictionary(id => id, id => $"Usuario {id:N}"[..12])
                : new Dictionary<Guid, string>());
}
