using CaeManager.Application.Common;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Fake in-memory de <see cref="IAlcanceDatosService"/> para tests —
/// respeta el mismo contrato que la implementación real
/// (<c>null</c> = sin restricción, lista = cartera, incluida vacía =
/// cartera sin asignar todavía), configurable directamente por el test que
/// lo use en vez de tener que simular un rol/usuario real.
/// </summary>
public class AlcanceDatosServiceFalso(
    IReadOnlyList<Guid>? clienteIds = null,
    IReadOnlyList<Guid>? centroIds = null,
    IReadOnlyList<Guid>? empresaIds = null,
    IReadOnlyList<Guid>? subcontrataIds = null,
    IReadOnlyList<Guid>? trabajadorIds = null,
    IReadOnlyList<Guid>? vehiculoIds = null,
    IReadOnlyList<Guid>? conexionesIntegracionAjenas = null) : IAlcanceDatosService
{
    public Task<bool> TieneAccesoTotalAsync(CancellationToken cancellationToken = default) => Task.FromResult(
        clienteIds is null && centroIds is null && empresaIds is null &&
        subcontrataIds is null && trabajadorIds is null && vehiculoIds is null);

    public Task<IReadOnlyList<Guid>?> ObtenerClienteIdsVisiblesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(clienteIds);

    public Task<IReadOnlyList<Guid>?> ObtenerCentroIdsVisiblesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(centroIds);

    public Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsVisiblesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(empresaIds);

    public Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsVisiblesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(subcontrataIds);

    public Task<IReadOnlyList<Guid>?> ObtenerTrabajadorIdsVisiblesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(trabajadorIds);

    public Task<IReadOnlyList<Guid>?> ObtenerVehiculoIdsVisiblesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(vehiculoIds);

    /// <summary>Por defecto visible (ninguna conexión marcada como ajena) — solo lo controla el test que pase <c>conexionesIntegracionAjenas</c>.</summary>
    public Task<bool> ConexionIntegracionVisibleAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(conexionesIntegracionAjenas is null || !conexionesIntegracionAjenas.Contains(conexionIntegracionId));
}
