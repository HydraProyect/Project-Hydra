using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Doble de <see cref="IAsignacionAutomaticaDePrincipal"/> que no asigna nada: para los arneses que
/// construyen a mano un comando de delegaciones o de cuentas y no tratan del principal de cartera.
/// </summary>
public sealed class AsignacionAutomaticaInerte : IAsignacionAutomaticaDePrincipal
{
    public Task<bool> AlAbrirOperacionAsync(AsignacionOperacion operacion, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task<bool> AlPrimerElegibleAsync(Guid usuarioId, Guid operadorTenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}
