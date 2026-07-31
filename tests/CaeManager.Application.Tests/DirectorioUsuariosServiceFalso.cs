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
}
