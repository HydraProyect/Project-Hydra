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
/// No es seguro para hilos; cada llamada crea la suya. Es <c>internal</c> a propósito, para que nadie la registre en el
/// contenedor ni la guarde en un campo: en Blazor Server un circuito dura horas y congelaría la Cartera.
/// </para>
///
/// <para>
/// Con <paramref name="comoConjunto"/> a <c>false</c> (una sola comprobación) no construye el conjunto y busca en la
/// lista, como las extensiones: convertir toda la Cartera para un único Id cuesta más de lo que ahorra.
/// </para>
/// </summary>
internal sealed class CarteraEnMemoria(IAlcanceDatosService alcance, bool comoConjunto = true)
{
    private readonly ConjuntoVisible _trabajadores = new(alcance.ObtenerTrabajadorIdsVisiblesAsync, comoConjunto);
    private readonly ConjuntoVisible _empresas = new(alcance.ObtenerEmpresaIdsVisiblesAsync, comoConjunto);
    private readonly ConjuntoVisible _centros = new(alcance.ObtenerCentroIdsVisiblesAsync, comoConjunto);
    private readonly ConjuntoVisible _vehiculos = new(alcance.ObtenerVehiculoIdsVisiblesAsync, comoConjunto);

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

    private sealed class ConjuntoVisible(Func<CancellationToken, Task<IReadOnlyList<Guid>?>> leer, bool comoConjunto)
    {
        private bool _leido;
        private IReadOnlyList<Guid>? _lista;
        private HashSet<Guid>? _conjunto;

        public async Task<bool> IncluyeAsync(Guid id, CancellationToken cancellationToken)
        {
            if (!_leido)
            {
                _lista = await leer(cancellationToken);
                _conjunto = comoConjunto ? _lista?.ToHashSet() : null;
                _leido = true;
            }

            // Lista nula = sin restricción; lista, incluida la vacía = Cartera restringida.
            if (_lista is null)
                return true;
            return _conjunto?.Contains(id) ?? _lista.Contains(id);
        }
    }
}
