using CaeManager.Application.Common;

namespace CaeManager.Application.Tests;

/// <summary>
/// Fake de <see cref="IDirectorioUsuariosService"/>. Por defecto acepta
/// cualquier usuario, para que los tests que no van de eso no tengan que
/// configurarlo; los que sí lo comprueban pasan <c>esVisible: false</c>.
/// </summary>
public class DirectorioUsuariosServiceFalso(bool esVisible = true, Guid? tenantDelUsuario = null)
    : IDirectorioUsuariosService
{
    public Task<bool> EsVisibleEnTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult(esVisible);

    /// <summary>
    /// El tenant al que el fake dice que pertenece el usuario. Null por defecto
    /// para que los tests que no van de esto no tengan que configurarlo; los
    /// que comprueban el invariante usuario↔operador lo fijan explícitamente.
    /// </summary>
    public Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult(tenantDelUsuario);

    /// <summary>Mismo criterio que arriba: si el fake acepta a cualquiera, resuelve un nombre sintético; si no, no resuelve ninguno.</summary>
    public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(
        IReadOnlyCollection<Guid> usuarioIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            esVisible
                ? usuarioIds.ToDictionary(id => id, id => $"Usuario {id:N}"[..12])
                : new Dictionary<Guid, string>());
}
