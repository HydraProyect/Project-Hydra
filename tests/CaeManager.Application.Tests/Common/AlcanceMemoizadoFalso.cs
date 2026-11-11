using CaeManager.Application.Common;
using CaeManager.Application.Tests.Clientes;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// Alcance memoizado como el del circuito en Blazor Server: la primera pregunta fija la respuesta hasta que alguien llama
/// a <see cref="Invalidar"/>. <see cref="Revocar"/> simula que otra operación cierra la Asignación de Cartera mientras
/// el comando espera el candado de la regla A-1: solo lo ve quien descarta la memoización y vuelve a preguntar.
/// </summary>
public sealed class AlcanceMemoizadoFalso(IAlcanceDatosService inicial) : IAlcanceDatosService, IInvalidadorAlcance
{
    private IAlcanceDatosService enLaBase = inicial;
    private IAlcanceDatosService? memoizado;

    public int Invalidaciones { get; private set; }

    /// <summary>A partir de aquí la base ya no da cartera; la memoización, si la hay, sigue con la de antes.</summary>
    public void Revocar() => enLaBase = new AlcanceDatosServiceFalso(tieneAccesoTotal: false);

    public void Invalidar()
    {
        Invalidaciones++;
        memoizado = null;
    }

    private IAlcanceDatosService Actual => memoizado ??= enLaBase;

    public Task<bool> TieneAccesoTotalAsync(CancellationToken cancellationToken = default) =>
        Actual.TieneAccesoTotalAsync(cancellationToken);

    public Task<IReadOnlyList<Guid>?> ObtenerClienteIdsVisiblesAsync(CancellationToken cancellationToken = default) =>
        Actual.ObtenerClienteIdsVisiblesAsync(cancellationToken);

    public Task<bool> OperaDesdeElLadoDeGestionAsync(CancellationToken cancellationToken = default) =>
        Actual.OperaDesdeElLadoDeGestionAsync(cancellationToken);

    public Task<IReadOnlyList<Guid>?> ObtenerCentroIdsVisiblesAsync(CancellationToken cancellationToken = default) =>
        Actual.ObtenerCentroIdsVisiblesAsync(cancellationToken);

    public Task<IReadOnlyList<Guid>?> ObtenerCentroIdsParaGestionAsync(CancellationToken cancellationToken = default) =>
        Actual.ObtenerCentroIdsParaGestionAsync(cancellationToken);

    public Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsVisiblesAsync(CancellationToken cancellationToken = default) =>
        Actual.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);

    public Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsParaGestionAsync(CancellationToken cancellationToken = default) =>
        Actual.ObtenerEmpresaIdsParaGestionAsync(cancellationToken);

    public Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsVisiblesAsync(CancellationToken cancellationToken = default) =>
        Actual.ObtenerSubcontrataIdsVisiblesAsync(cancellationToken);

    public Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsParaGestionAsync(CancellationToken cancellationToken = default) =>
        Actual.ObtenerSubcontrataIdsParaGestionAsync(cancellationToken);

    public Task<bool> SubcontrataEliminadaParaGestionVisibleAsync(Guid subcontrataId, CancellationToken cancellationToken = default) =>
        Actual.SubcontrataEliminadaParaGestionVisibleAsync(subcontrataId, cancellationToken);

    public Task<IReadOnlyList<Guid>?> ObtenerTrabajadorIdsVisiblesAsync(CancellationToken cancellationToken = default) =>
        Actual.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);

    public Task<IReadOnlyList<Guid>?> ObtenerVehiculoIdsVisiblesAsync(CancellationToken cancellationToken = default) =>
        Actual.ObtenerVehiculoIdsVisiblesAsync(cancellationToken);

    public Task<bool> ConexionIntegracionVisibleAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default) =>
        Actual.ConexionIntegracionVisibleAsync(conexionIntegracionId, cancellationToken);
}
