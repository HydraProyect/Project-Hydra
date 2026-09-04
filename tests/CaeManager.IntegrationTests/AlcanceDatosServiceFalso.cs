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
    IReadOnlyList<Guid>? conexionesIntegracionAjenas = null,
    IReadOnlyList<Guid>? empresaIdsParaGestion = null,
    IReadOnlyList<Guid>? subcontrataIdsParaGestion = null) : IAlcanceDatosService
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

    /// <summary>
    /// Por defecto igual que el alcance de lectura — solo el test que simule a
    /// un usuario de portal (rol Cliente) pasa <paramref name="empresaIdsParaGestion"/>
    /// vacío, que es lo que hace la implementación real para ese rol.
    /// </summary>
    public Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsParaGestionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(empresaIdsParaGestion ?? empresaIds);

    public Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsVisiblesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(subcontrataIds);

    /// <summary>
    /// Por defecto igual que el alcance de lectura — solo el test que simule a
    /// un usuario de portal (rol Cliente) pasa <paramref name="subcontrataIdsParaGestion"/>
    /// vacío, que es lo que hace la implementación real para ese rol (REC-159).
    /// </summary>
    public Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsParaGestionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(subcontrataIdsParaGestion ?? subcontrataIds);

    public Task<IReadOnlyList<Guid>?> ObtenerTrabajadorIdsVisiblesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(trabajadorIds);

    public Task<IReadOnlyList<Guid>?> ObtenerVehiculoIdsVisiblesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(vehiculoIds);

    /// <summary>Por defecto visible (ninguna conexión marcada como ajena) — solo lo controla el test que pase <c>conexionesIntegracionAjenas</c>.</summary>
    public Task<bool> ConexionIntegracionVisibleAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(conexionesIntegracionAjenas is null || !conexionesIntegracionAjenas.Contains(conexionIntegracionId));
}
