using CaeManager.Application.Common;

namespace CaeManager.Application.Tests.Clientes;

public class AlcanceDatosServiceFalso(
    bool tieneAccesoTotal = true, IReadOnlyList<Guid>? clienteIdsVisibles = null, IReadOnlyList<Guid>? trabajadorIdsVisibles = null,
    bool conexionIntegracionVisible = true, IReadOnlyList<Guid>? empresaIdsVisibles = null, IReadOnlyList<Guid>? centroIdsVisibles = null,
    IReadOnlyList<Guid>? subcontrataIdsVisibles = null, IReadOnlyList<Guid>? conexionesIntegracionAjenas = null,
    IReadOnlyList<Guid>? empresaIdsParaGestion = null)
    : IAlcanceDatosService
{
    public Task<bool> TieneAccesoTotalAsync(CancellationToken cancellationToken = default) => Task.FromResult(tieneAccesoTotal);

    public Task<IReadOnlyList<Guid>?> ObtenerClienteIdsVisiblesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(tieneAccesoTotal ? null : clienteIdsVisibles ?? []);

    /// <summary>Por defecto null (sin restricción), igual que antes de que este parámetro existiera — solo lo controla el test que lo pase explícitamente.</summary>
    public Task<IReadOnlyList<Guid>?> ObtenerCentroIdsVisiblesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(tieneAccesoTotal ? null : centroIdsVisibles ?? []);

    /// <summary>Por defecto null (sin restricción), igual que antes de que este parámetro existiera — solo lo controla el test que lo pase explícitamente.</summary>
    public Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsVisiblesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(tieneAccesoTotal ? null : empresaIdsVisibles ?? []);

    /// <summary>
    /// Por defecto igual que el alcance de lectura: solo el test que quiera
    /// simular a un usuario de portal pasa <c>empresaIdsParaGestion</c> vacío.
    /// </summary>
    public Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsParaGestionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(empresaIdsParaGestion ?? (tieneAccesoTotal ? null : empresaIdsVisibles ?? []));

    /// <summary>Por defecto null (sin restriccion), como los demas agregados.</summary>
    public Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsVisiblesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(tieneAccesoTotal ? null : subcontrataIdsVisibles ?? []);

    /// <summary>Por defecto null (sin restricción), igual que antes de que este parámetro existiera — solo lo controla el test que lo pase explícitamente.</summary>
    public Task<IReadOnlyList<Guid>?> ObtenerTrabajadorIdsVisiblesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(trabajadorIdsVisibles);

    public Task<IReadOnlyList<Guid>?> ObtenerVehiculoIdsVisiblesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Guid>?>(null);

    /// <summary>Si el test pasa <c>conexionesIntegracionAjenas</c>, decide por Id (una conexión ajena, el resto visibles); si no, aplica el flag global de siempre.</summary>
    public Task<bool> ConexionIntegracionVisibleAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(conexionesIntegracionAjenas is not null
            ? !conexionesIntegracionAjenas.Contains(conexionIntegracionId)
            : conexionIntegracionVisible);
}
