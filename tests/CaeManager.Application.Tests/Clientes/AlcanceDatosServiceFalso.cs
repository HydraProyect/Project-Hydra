using CaeManager.Application.Common;

namespace CaeManager.Application.Tests.Clientes;

public class AlcanceDatosServiceFalso(bool tieneAccesoTotal = true, IReadOnlyList<Guid>? clienteIdsVisibles = null) : IAlcanceDatosService
{
    public Task<bool> TieneAccesoTotalAsync(CancellationToken cancellationToken = default) => Task.FromResult(tieneAccesoTotal);

    public Task<IReadOnlyList<Guid>?> ObtenerClienteIdsVisiblesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(tieneAccesoTotal ? null : clienteIdsVisibles ?? []);

    public Task<IReadOnlyList<Guid>?> ObtenerCentroIdsVisiblesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Guid>?>(null);

    public Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsVisiblesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Guid>?>(null);

    public Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsVisiblesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Guid>?>(null);

    public Task<IReadOnlyList<Guid>?> ObtenerTrabajadorIdsVisiblesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Guid>?>(null);

    public Task<IReadOnlyList<Guid>?> ObtenerVehiculoIdsVisiblesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Guid>?>(null);
}
