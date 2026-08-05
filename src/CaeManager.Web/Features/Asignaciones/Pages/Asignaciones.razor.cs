using CaeManager.Application.Alertas;
using CaeManager.Application.Asignaciones.Commands.CrearAsignaciones;
using CaeManager.Application.Asignaciones.Commands.DarDeBajaAsignacion;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignaciones;
using CaeManager.Application.Asignaciones.Queries.ObtenerDocumentosFaltantesParaAsignacion;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.QuickGrid;

namespace CaeManager.Web.Features.Asignaciones.Pages;

public partial class Asignaciones : ComponentBase
{
    private readonly PaginationState _paginacion = new() { ItemsPerPage = 20 };
    private QuickGrid<AsignacionListaDto>? _grid;

    private string _busqueda = string.Empty;
    private string _estadoFiltro = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    /// <summary>
    /// Opciones del filtro de estado: una Asignación no tiene columna de
    /// estado — "activa" es no tener FechaBaja (ver DOMAIN.md).
    /// </summary>
    private static readonly IReadOnlyList<OpcionEstado> OpcionesEstado =
    [
        new("Activa", "Activa"),
        new("DeBaja", "De baja")
    ];

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "estado")]
    public string? EstadoInicial { get; set; }

    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresDisponibles = [];
    private IReadOnlyList<CentroSelectorDto> _centrosDisponibles = [];

    private static readonly IReadOnlyList<PestanaDefinicion> _pestanasAlta =
    [
        new("Lista", "Lista"),
        new("Matriz", "Matriz")
    ];

    private bool _drawerVisible;
    private string _vistaAlta = "Lista";
    private readonly HashSet<Guid> _trabajadorIdsSeleccionados = [];
    private readonly HashSet<Guid> _centroIdsSeleccionados = [];
    private readonly HashSet<(Guid TrabajadorId, Guid CentroId)> _celdasExcluidas = [];
    private string _fechaAlta = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private IReadOnlyList<DocumentoFaltanteDto> _documentosFaltantes = [];

    private IReadOnlyList<ElementoSeleccionable> _trabajadoresComoOpciones =>
        _trabajadoresDisponibles.Select(t => new ElementoSeleccionable(t.Id, $"{t.NombreCompleto} ({t.Dni})")).ToList();

    private IReadOnlyList<ElementoSeleccionable> _centrosComoOpciones =>
        _centrosDisponibles.Select(c => new ElementoSeleccionable(c.Id, $"{c.Nombre} ({c.ClienteRazonSocial})")).ToList();

    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresSeleccionadosOrdenados =>
        _trabajadoresDisponibles.Where(t => _trabajadorIdsSeleccionados.Contains(t.Id))
            .OrderBy(t => t.NombreCompleto).ToList();

    private IReadOnlyList<CentroSelectorDto> _centrosSeleccionadosOrdenados =>
        _centrosDisponibles.Where(c => _centroIdsSeleccionados.Contains(c.Id))
            .OrderBy(c => c.Nombre).ToList();

    private bool _darDeBajaVisible;
    private Guid _idParaBaja;
    private string _trabajadorParaBaja = string.Empty;
    private string _centroParaBaja = string.Empty;
    private string _fechaBaja = string.Empty;
    private bool _procesandoBaja;

    private GridItemsProvider<AsignacionListaDto>? _proveedorElementos;

    // Delegado estable — ver Clientes.razor.cs (bucle de recargas de QuickGrid).
    protected override void OnInitialized() => _proveedorElementos = ProveerElementosAsync;

    /// <summary>
    /// La URL es la fuente de verdad del filtro, no solo su semilla inicial
    /// (P1-18 de docs/business/MATURITY_REVIEW.md) — mismo patrón que el resto
    /// de listados.
    /// </summary>
    protected override void OnParametersSet()
    {
        var deLaUrl = OpcionesEstado.Any(o => o.Valor == EstadoInicial) ? EstadoInicial! : string.Empty;
        if (deLaUrl != _estadoFiltro)
            _estadoFiltro = deLaUrl;
    }

    private async Task CambiarEstadoAsync(string valor)
    {
        _estadoFiltro = valor;
        NavigationManager.ActualizarFiltroEnUrl("estado", valor);
        await RecargarAsync();
    }

    private async ValueTask<GridItemsProviderResult<AsignacionListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<AsignacionListaDto> request)
    {
        _cargando = true;
        _errorCarga = false;

        try
        {
            var pagina = (request.StartIndex / _paginacion.ItemsPerPage) + 1;
            var (ordenarPor, descendente) = LecturaOrden.Leer(request);

            var resultado = await Mediator.Send(new ObtenerAsignacionesQuery(
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                Activa: _estadoFiltro switch { "Activa" => true, "DeBaja" => false, _ => (bool?)null },
                Pagina: pagina,
                TamanoPagina: _paginacion.ItemsPerPage,
                OrdenarPor: ordenarPor,
                Descendente: descendente));

            _totalElementos = resultado.TotalElementos;

            return GridItemsProviderResult.From(resultado.Elementos.ToList(), resultado.TotalElementos);
        }
        catch (Exception)
        {
            _errorCarga = true;
            return GridItemsProviderResult.From(new List<AsignacionListaDto>(), 0);
        }
        finally
        {
            _cargando = false;
            StateHasChanged();
        }
    }

    private async Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        await RecargarAsync();
    }

    private async Task RecargarAsync()
    {
        await _paginacion.SetCurrentPageIndexAsync(0);

        if (_grid is not null)
            await _grid.RefreshDataAsync();

        StateHasChanged();
    }

    private async Task AbrirCrearAsync()
    {
        _trabajadoresDisponibles = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery());
        _centrosDisponibles = await Mediator.Send(new ObtenerCentrosParaSelectorQuery());

        _vistaAlta = "Lista";
        _trabajadorIdsSeleccionados.Clear();
        _centroIdsSeleccionados.Clear();
        _celdasExcluidas.Clear();
        _documentosFaltantes = [];
        _fechaAlta = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private async Task AlternarTrabajadorAsync(Guid trabajadorId, bool marcado)
    {
        if (marcado)
            _trabajadorIdsSeleccionados.Add(trabajadorId);
        else
        {
            _trabajadorIdsSeleccionados.Remove(trabajadorId);
            _celdasExcluidas.RemoveWhere(c => c.TrabajadorId == trabajadorId);
        }

        await ActualizarPreflightAsync();
    }

    private async Task AlternarCentroAsync(Guid centroId, bool marcado)
    {
        if (marcado)
            _centroIdsSeleccionados.Add(centroId);
        else
        {
            _centroIdsSeleccionados.Remove(centroId);
            _celdasExcluidas.RemoveWhere(c => c.CentroId == centroId);
        }

        await ActualizarPreflightAsync();
    }

    private void AlternarCeldaMatriz(Guid trabajadorId, Guid centroId, bool incluida)
    {
        if (incluida)
            _celdasExcluidas.Remove((trabajadorId, centroId));
        else
            _celdasExcluidas.Add((trabajadorId, centroId));
    }

    /// <summary>
    /// Preflight no bloqueante (Fase B): qué documentos obligatorios le
    /// faltarían a cada trabajador en cada centro si se confirma el lote tal
    /// cual está seleccionado. Se recalcula sobre el cruce completo de la
    /// selección — con exclusiones puntuales de la Matriz puede sobrar algún
    /// aviso, aceptable porque es solo informativo (nunca bloquea Guardar).
    /// </summary>
    private async Task ActualizarPreflightAsync()
    {
        if (_trabajadorIdsSeleccionados.Count == 0 || _centroIdsSeleccionados.Count == 0)
        {
            _documentosFaltantes = [];
            return;
        }

        _documentosFaltantes = await Mediator.Send(new ObtenerDocumentosFaltantesParaAsignacionQuery(
            _trabajadorIdsSeleccionados.ToList(), _centroIdsSeleccionados.ToList()));
    }

    private async Task GuardarAsync()
    {
        _guardando = true;
        _mensajeErrorFormulario = null;

        try
        {
            if (_trabajadorIdsSeleccionados.Count == 0)
            {
                _mensajeErrorFormulario = "Selecciona al menos un trabajador.";
                return;
            }

            if (_centroIdsSeleccionados.Count == 0)
            {
                _mensajeErrorFormulario = "Selecciona al menos un centro.";
                return;
            }

            if (!DateOnly.TryParse(_fechaAlta, out var fechaAlta))
            {
                _mensajeErrorFormulario = "Introduce una fecha de alta válida.";
                return;
            }

            var creadas = 0;
            var yaActivas = 0;
            var errores = new List<string>();

            if (_celdasExcluidas.Count == 0)
            {
                var resultado = await Mediator.Send(new CrearAsignacionesCommand(
                    _trabajadorIdsSeleccionados.ToList(), _centroIdsSeleccionados.ToList(), fechaAlta));

                if (resultado.EsFallido)
                {
                    _mensajeErrorFormulario = resultado.Error.Mensaje;
                    return;
                }

                creadas = resultado.Valor.Creadas;
                yaActivas = resultado.Valor.YaActivas;
                errores.AddRange(resultado.Valor.Errores);
            }
            else
            {
                // Con exclusiones puntuales de la Matriz, el cruce ya no es
                // uniforme — un lote por Centro con los Trabajadores que le
                // quedan tras quitar las celdas desmarcadas de esa columna.
                foreach (var centroId in _centroIdsSeleccionados)
                {
                    var trabajadorIdsParaCentro = _trabajadorIdsSeleccionados
                        .Where(t => !_celdasExcluidas.Contains((t, centroId)))
                        .ToList();

                    if (trabajadorIdsParaCentro.Count == 0) continue;

                    var resultado = await Mediator.Send(new CrearAsignacionesCommand(trabajadorIdsParaCentro, [centroId], fechaAlta));

                    if (resultado.EsFallido)
                    {
                        _mensajeErrorFormulario = resultado.Error.Mensaje;
                        return;
                    }

                    creadas += resultado.Valor.Creadas;
                    yaActivas += resultado.Valor.YaActivas;
                    errores.AddRange(resultado.Valor.Errores);
                }
            }

            var resumen = $"{creadas} asignación(es) creada(s)" + (yaActivas > 0 ? $", {yaActivas} ya estaban activas." : ".");
            ToastService.Mostrar(resumen, errores.Count > 0 ? TonoToast.Advertencia : TonoToast.Exito);
            foreach (var error in errores)
                ToastService.Mostrar(error, TonoToast.Advertencia);

            _drawerVisible = false;
            await RecargarAsync();
        }
        catch (ValidationException)
        {
            _mensajeErrorFormulario = "Revisa los datos introducidos.";
        }
        catch (Exception)
        {
            _mensajeErrorFormulario = "No pudimos guardar los cambios. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardando = false;
        }
    }

    private void AbrirDarDeBaja(Guid id, string trabajadorNombre, string centroNombre)
    {
        _idParaBaja = id;
        _trabajadorParaBaja = trabajadorNombre;
        _centroParaBaja = centroNombre;
        _fechaBaja = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        _darDeBajaVisible = true;
    }

    private async Task ConfirmarDarDeBajaAsync()
    {
        _procesandoBaja = true;

        try
        {
            if (!DateOnly.TryParse(_fechaBaja, out var fechaBaja))
            {
                ToastService.Mostrar("Introduce una fecha de baja válida.", TonoToast.Error);
                return;
            }

            var resultado = await Mediator.Send(new DarDeBajaAsignacionCommand(_idParaBaja, fechaBaja));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                ToastService.Mostrar("Trabajador dado de baja correctamente.", TonoToast.Exito);
                _darDeBajaVisible = false;
                await RecargarAsync();
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos procesar la baja. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _procesandoBaja = false;
        }
    }
}
