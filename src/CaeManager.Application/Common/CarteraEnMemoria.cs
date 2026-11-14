namespace CaeManager.Application.Common;

/// <summary>
/// Las mismas comprobaciones de <see cref="AlcanceDatosServiceExtensions"/> (lista nula = sin restricción; lista,
/// incluida la vacía = Cartera restringida) para quien decide muchos registros en memoria en una sola llamada. Cada
/// lista de alcance se lee una vez, al primer uso, y se guarda como <see cref="HashSet{T}"/>: cada comprobación cuesta
/// O(1) en vez de recorrer la lista. Sin esto, decidir N registros contra una Cartera de C Ids cuesta O(N·C) (hallazgo
/// de Codex, ronda 4 de la PR #2222, sobre la puerta del lote de generación). La semántica no cambia: solo dónde se
/// busca el Id.
///
/// <para>
/// Vive lo que dura una llamada (una puerta, un lote de registros), no más: el alcance puede cambiar entre peticiones.
/// No es seguro para hilos; cada llamada crea la suya.
/// </para>
/// </summary>
public sealed class CarteraEnMemoria(IAlcanceDatosService alcance)
{
    private readonly ConjuntoVisible _trabajadores = new(alcance.ObtenerTrabajadorIdsVisiblesAsync);
    private readonly ConjuntoVisible _empresas = new(alcance.ObtenerEmpresaIdsVisiblesAsync);
    private readonly ConjuntoVisible _centros = new(alcance.ObtenerCentroIdsVisiblesAsync);
    private readonly ConjuntoVisible _vehiculos = new(alcance.ObtenerVehiculoIdsVisiblesAsync);

    /// <summary>Lo mismo que <see cref="AlcanceDatosServiceExtensions.TrabajadorVisibleAsync"/>.</summary>
    public Task<bool> TrabajadorVisibleAsync(Guid trabajadorId, CancellationToken cancellationToken = default) =>
        _trabajadores.IncluyeAsync(trabajadorId, cancellationToken);

    /// <summary>Lo mismo que <see cref="AlcanceDatosServiceExtensions.EmpresaVisibleAsync"/>.</summary>
    public Task<bool> EmpresaVisibleAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
        _empresas.IncluyeAsync(empresaId, cancellationToken);

    /// <summary>Lo mismo que <see cref="AlcanceDatosServiceExtensions.CentroVisibleAsync"/>.</summary>
    public Task<bool> CentroVisibleAsync(Guid centroId, CancellationToken cancellationToken = default) =>
        _centros.IncluyeAsync(centroId, cancellationToken);

    /// <summary>Lo mismo que <see cref="AlcanceDatosServiceExtensions.VehiculoVisibleAsync"/>.</summary>
    public Task<bool> VehiculoVisibleAsync(Guid vehiculoId, CancellationToken cancellationToken = default) =>
        _vehiculos.IncluyeAsync(vehiculoId, cancellationToken);

    private sealed class ConjuntoVisible(Func<CancellationToken, Task<IReadOnlyList<Guid>?>> leer)
    {
        private bool _leido;
        private HashSet<Guid>? _ids;

        public async Task<bool> IncluyeAsync(Guid id, CancellationToken cancellationToken)
        {
            if (!_leido)
            {
                _ids = (await leer(cancellationToken))?.ToHashSet();
                _leido = true;
            }

            return _ids is null || _ids.Contains(id);
        }
    }
}
