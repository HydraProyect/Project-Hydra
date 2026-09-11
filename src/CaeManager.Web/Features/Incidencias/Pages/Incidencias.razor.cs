using CaeManager.Web.Components;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Incidencias.Commands.CrearIncidencia;
using CaeManager.Application.Incidencias.Commands.EditarIncidencia;
using CaeManager.Application.Incidencias.Commands.EliminarIncidencia;
using CaeManager.Application.Incidencias.Commands.EliminarIncidencias;
using CaeManager.Application.Incidencias.Commands.MarcarResueltaIncidencia;
using CaeManager.Application.Incidencias.Queries.ObtenerIncidenciaPorId;
using CaeManager.Application.Incidencias.Queries.ObtenerIncidencias;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Incidencias;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.QuickGrid;

namespace CaeManager.Web.Features.Incidencias.Pages;

public partial class Incidencias : ComponentBase
{
    private readonly PaginationState _paginacion = new() { ItemsPerPage = 20 };

    // H2 (docs/ux-audit/02-clientes.md): paginador único en español, ver Clientes.razor.cs.
    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_totalElementos / (double)_paginacion.ItemsPerPage));

    private Task CambiarPaginaAsync(int pagina) => _paginacion.SetCurrentPageIndexAsync(pagina - 1);

    // H5 (docs/ux-audit/05-trabajadores-vehiculos.md): selector de tamaño de página, compartido por PaginadorSimple.razor.
    private async Task CambiarTamanoPaginaAsync(int tamano)
    {
        _paginacion.ItemsPerPage = tamano;
        await _paginacion.SetCurrentPageIndexAsync(0);
        if (_grid is not null)
            await _grid.RefreshDataAsync();
    }

    private QuickGrid<IncidenciaListaDto>? _grid;

    private string _busqueda = string.Empty;
    private string _estadoFiltro = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    /// <summary>
    /// Número de la carga de la lista en curso. Cada llamada al proveedor lo
    /// incrementa y, tras el <c>await</c>, solo escribe estado si sigue siendo
    /// la última: una respuesta lenta de un filtro anterior no puede pisar el
    /// total, la página ni el error de la consulta vigente. Lo que se protege
    /// son los campos que esta página escribe al lado de las filas —el total
    /// decide qué estado vacío se pinta y qué dice el recuento—; la guarda no
    /// depende de lo que QuickGrid haga con las filas de una carga superada.
    /// </summary>
    private int _generacionCarga;

    /// <summary>
    /// Mismo mecanismo para abrir el formulario: abrir «Editar» en una fila y,
    /// antes de que llegue su detalle, abrirlo en otra — o pulsar «Nueva
    /// incidencia» — no puede acabar con el formulario de la primera a la vista
    /// y guardando sobre ella.
    /// </summary>
    private int _generacionFormulario;

    private IReadOnlyList<CentroSelectorDto> _centrosDisponibles = [];
    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresDisponibles = [];

    private bool _drawerVisible;
    private Guid? _editandoId;
    // Version del registro tal como se abrio: vuelve en el Command para
    // detectar que otra persona guardo mientras el formulario estaba abierto.
    private Guid _versionEditando;
    private string _centroId = string.Empty;
    private string _centroNombreEnEdicion = string.Empty;
    private string _trabajadorId = string.Empty;
    private string _tipo = nameof(TipoIncidencia.Accidente);
    private string _gravedad = nameof(GravedadIncidencia.Leve);
    private string _fechaOcurrencia = string.Empty;
    private string _descripcion = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _centroAEliminar = string.Empty;
    private bool _eliminando;

    private readonly HashSet<Guid> _seleccionados = [];

    /// <summary>
    /// Los checkboxes de fila solo se pintan con esto activo (Centro 360,
    /// PLAN-EJECUCION-UX.md § 0.9) — son ruido permanente para una acción
    /// ocasional. Apagarlo limpia la selección: dejar filas marcadas que ya
    /// no se ven dejaría la barra de acciones en lote apuntando a algo
    /// invisible.
    /// </summary>
    private bool _seleccionMultiple;

    private void AlternarSeleccionMultiple(bool activa)
    {
        _seleccionMultiple = activa;
        if (!activa)
            _seleccionados.Clear();
    }
    private List<IncidenciaListaDto> _elementosPagina = [];
    private Guid? _idEnfocado;
    private bool _eliminandoLote;
    private bool _confirmarEliminarLoteVisible;

    /// <summary>
    /// Mensaje del conflicto de versión, con el género de «incidencia». El que
    /// trae <see cref="ConcurrenciaOptimista"/> es genérico y en masculino
    /// («mientras lo editabas») para cualquier entidad.
    /// </summary>
    internal const string MensajeConflictoVersion =
        "Otra persona guardó esta incidencia mientras la tenías abierta. Vuelve a abrirla para no pisar sus cambios.";

    private static TonoBadge GravedadTono(GravedadIncidencia gravedad) => gravedad switch
    {
        GravedadIncidencia.Leve => TonoBadge.Advertencia,
        GravedadIncidencia.Grave => TonoBadge.Peligro,
        GravedadIncidencia.MuyGrave => TonoBadge.Peligro,
        _ => TonoBadge.Neutro
    };

    private static string ObtenerEtiquetaTipo(TipoIncidencia tipo) => tipo switch
    {
        TipoIncidencia.Accidente => "Accidente",
        TipoIncidencia.Incumplimiento => "Incumplimiento",
        _ => tipo.ToString()
    };

    private static string ObtenerEtiquetaGravedad(GravedadIncidencia gravedad) => gravedad switch
    {
        GravedadIncidencia.Leve => "Leve",
        GravedadIncidencia.Grave => "Grave",
        GravedadIncidencia.MuyGrave => "Muy grave",
        _ => gravedad.ToString()
    };

    private GridItemsProvider<IncidenciaListaDto>? _proveedorElementos;

    /// <summary>
    /// Una Incidencia no tiene enum de estado: su estado es <c>Resuelta</c>.
    /// Esto sustituye al antiguo checkbox "Solo sin resolver", que solo dejaba
    /// filtrar en un sentido — no había forma de ver únicamente las resueltas.
    /// </summary>
    private static readonly IReadOnlyList<OpcionEstado> OpcionesEstado =
    [
        new("SinResolver", "Sin resolver"),
        new("Resuelta", "Resuelta")
    ];

    private string EtiquetaFiltroEstado => _estadoFiltro == "Resuelta" ? "Estado: resueltas" : "Estado: sin resolver";

    /// <summary>
    /// El recuento solo se pinta con la carga terminada: durante una recarga el
    /// total todavía es el del filtro anterior, y «con el filtro actual» sería
    /// falso justo en ese instante. Con cero lo dicen ya los estados vacíos.
    /// </summary>
    private bool MostrarRecuento => !_cargando && _totalElementos > 0;

    /// <summary>
    /// Es el total de servidor YA filtrado: la pantalla no sabe cuántas hay sin
    /// filtro, así que «con el filtro actual» solo se añade cuando lo hay.
    /// </summary>
    private string TextoRecuento =>
        (_totalElementos == 1 ? "1 incidencia" : $"{_totalElementos} incidencias")
        + (HayFiltrosActivos ? " con el filtro actual" : string.Empty);

    /// <summary>
    /// Esqueleto solo cuando no hay nada que enseñar todavía (primera carga, o
    /// recarga tras un cero). Con filas ya pintadas, una recarga las deja a la
    /// vista en vez de parpadear a un esqueleto en cada cambio de página.
    /// </summary>
    private bool MostrarEsqueleto => _cargando && _elementosPagina.Count == 0;

    private bool OcultarTabla => MostrarEsqueleto || (_totalElementos == 0 && !_cargando);

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "estado")]
    public string? EstadoInicial { get; set; }

    // Delegado estable — ver Clientes.razor.cs (bucle de recargas de QuickGrid).
    protected override void OnInitialized() => _proveedorElementos = ProveerElementosAsync;

    /// <summary>La URL es la fuente de verdad del filtro (P1-18) — ver el resto de listados.</summary>
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

    /// <summary>El chip quita solo el estado; la búsqueda se queda como está.</summary>
    private Task QuitarFiltroEstadoAsync() => CambiarEstadoAsync(string.Empty);

    private async ValueTask<GridItemsProviderResult<IncidenciaListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<IncidenciaListaDto> request)
    {
        var generacion = ++_generacionCarga;
        _cargando = true;
        _errorCarga = false;

        try
        {
            var pagina = (request.StartIndex / _paginacion.ItemsPerPage) + 1;

            var (ordenarPor, descendente) = LecturaOrden.Leer(request);

            // La consulta se construye con los filtros de ESTE instante: si
            // cambian mientras llega la respuesta, lo que se descarta es la
            // respuesta, no se reinterpreta con los filtros nuevos.
            var resultado = await Mediator.Send(new ObtenerIncidenciasQuery(
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                SoloSinResolver: false,
                Resuelta: _estadoFiltro switch { "Resuelta" => true, "SinResolver" => false, _ => (bool?)null },
                Pagina: pagina,
                TamanoPagina: _paginacion.ItemsPerPage,
                OrdenarPor: ordenarPor,
                Descendente: descendente), request.CancellationToken);

            if (generacion != _generacionCarga)
                return GridItemsProviderResult.From(new List<IncidenciaListaDto>(), 0);

            _totalElementos = resultado.TotalElementos;

            var elementos = resultado.Elementos.ToList();
            _elementosPagina = elementos;
            _seleccionados.Clear();
            _idEnfocado = null;

            return GridItemsProviderResult.From(elementos, resultado.TotalElementos);
        }
        catch (Exception)
        {
            // Una carga ya superada que falla (o que QuickGrid canceló) no es
            // un error de la lista que se está mirando.
            if (generacion == _generacionCarga)
                _errorCarga = true;
            return GridItemsProviderResult.From(new List<IncidenciaListaDto>(), 0);
        }
        finally
        {
            if (generacion == _generacionCarga)
                _cargando = false;
            StateHasChanged();
        }
    }

    private async Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        await RecargarAsync();
    }

    /// <summary>
    /// Los dos filtros de la barra. Separa "todavía no hay incidencias" de
    /// "ninguna con estos filtros": con "Sin resolver" puesto, la primera
    /// frase hace creer que nunca ha pasado nada en ningún centro.
    /// </summary>
    private bool HayFiltrosActivos =>
        !string.IsNullOrWhiteSpace(_busqueda) || !string.IsNullOrWhiteSpace(_estadoFiltro);

    /// <summary>
    /// Quita los dos filtros en una sola recarga. El estado vive además en la
    /// URL y se limpia allí: <see cref="OnParametersSet"/> re-sincroniza desde
    /// ella en cada navegación dentro de la página.
    /// </summary>
    private async Task LimpiarFiltrosAsync()
    {
        _busqueda = string.Empty;
        _estadoFiltro = string.Empty;
        NavigationManager.ActualizarFiltroEnUrl("estado", string.Empty);
        await RecargarAsync();
    }

    private async Task RecargarAsync()
    {
        await _paginacion.SetCurrentPageIndexAsync(0);

        if (_grid is not null)
            await _grid.RefreshDataAsync();

        StateHasChanged();
    }

    /// <summary>
    /// Con el error a la vista la tabla no está en el árbol: su QuickGrid se
    /// desmontó y refrescar la referencia vieja no garantiza nada. Se vuelve a
    /// montar una tabla nueva, que hace su propia primera carga.
    /// </summary>
    private async Task ReintentarAsync()
    {
        _errorCarga = false;
        _cargando = true;
        _elementosPagina = [];
        await _paginacion.SetCurrentPageIndexAsync(0);
        StateHasChanged();
    }

    private async Task AbrirCrearAsync()
    {
        var generacion = ++_generacionFormulario;

        var centros = await Mediator.Send(new ObtenerCentrosParaSelectorQuery());
        var trabajadores = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery());
        if (generacion != _generacionFormulario) return;

        _centrosDisponibles = centros;
        _trabajadoresDisponibles = trabajadores;
        _editandoId = null;
        _centroId = string.Empty;
        _centroNombreEnEdicion = string.Empty;
        _trabajadorId = string.Empty;
        _tipo = nameof(TipoIncidencia.Accidente);
        _gravedad = nameof(GravedadIncidencia.Leve);
        _fechaOcurrencia = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        _descripcion = string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _guardando = false;
        _drawerVisible = true;
    }

    private async Task AbrirEditarAsync(Guid id)
    {
        var generacion = ++_generacionFormulario;

        var trabajadores = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery());
        var incidencia = await Mediator.Send(new ObtenerIncidenciaPorIdQuery(id));
        if (generacion != _generacionFormulario) return;

        if (incidencia is null)
        {
            ToastService.Mostrar("No encontramos esta incidencia. Puede que ya se haya eliminado.", TonoToast.Error);
            await RecargarAsync();
            return;
        }

        _trabajadoresDisponibles = trabajadores;
        _editandoId = incidencia.Id;
        _versionEditando = incidencia.Version;
        _centroId = incidencia.CentroId.ToString();
        _centroNombreEnEdicion = incidencia.CentroNombre;
        _trabajadorId = incidencia.TrabajadorId?.ToString() ?? string.Empty;
        _tipo = incidencia.Tipo.ToString();
        _gravedad = incidencia.Gravedad.ToString();
        _fechaOcurrencia = incidencia.FechaOcurrencia.ToString("yyyy-MM-dd");
        _descripcion = incidencia.Descripcion;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _guardando = false;
        _drawerVisible = true;
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        if (!visible)
            _generacionFormulario++;
        return Task.CompletedTask;
    }

    private static string MensajeDeFallo(Error error) =>
        error.Codigo == ConcurrenciaOptimista.CodigoConflicto ? MensajeConflictoVersion : error.Mensaje;

    private async Task GuardarAsync()
    {
        var generacionFormulario = _generacionFormulario;
        var editandoId = _editandoId;
        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();

        try
        {
            if (!DateOnly.TryParse(_fechaOcurrencia, out var fechaOcurrencia))
            {
                _mensajeErrorFormulario = "La fecha no es válida.";
                return;
            }

            if (!Enum.TryParse<TipoIncidencia>(_tipo, out var tipo))
            {
                _mensajeErrorFormulario = "Selecciona un tipo válido.";
                return;
            }

            if (!Enum.TryParse<GravedadIncidencia>(_gravedad, out var gravedad))
            {
                _mensajeErrorFormulario = "Selecciona una gravedad válida.";
                return;
            }

            var trabajadorId = Guid.TryParse(_trabajadorId, out var tId) ? tId : (Guid?)null;
            string? mensajeError;

            if (editandoId is null)
            {
                if (!Guid.TryParse(_centroId, out var centroId))
                {
                    _mensajeErrorFormulario = "Selecciona un centro.";
                    return;
                }

                var resultado = await Mediator.Send(new CrearIncidenciaCommand(centroId, trabajadorId, tipo, gravedad, fechaOcurrencia, _descripcion));
                mensajeError = resultado.EsFallido ? MensajeDeFallo(resultado.Error) : null;
            }
            else
            {
                var resultado = await Mediator.Send(new EditarIncidenciaCommand(editandoId.Value, trabajadorId, tipo, gravedad, fechaOcurrencia, _descripcion, _versionEditando));
                mensajeError = resultado.EsFallido ? MensajeDeFallo(resultado.Error) : null;
            }

            if (mensajeError is not null)
            {
                if (generacionFormulario == _generacionFormulario)
                    _mensajeErrorFormulario = mensajeError;
                return;
            }

            ToastService.Mostrar(
                editandoId is null ? "Incidencia creada correctamente." : "Incidencia actualizada correctamente.",
                TonoToast.Exito);

            if (generacionFormulario == _generacionFormulario)
                _drawerVisible = false;
            await RecargarAsync();
        }
        catch (ValidationException ex)
        {
            if (generacionFormulario == _generacionFormulario)
            {
                _erroresCampo = ex.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.First().ErrorMessage);
            }
        }
        catch (Exception)
        {
            if (generacionFormulario == _generacionFormulario)
                _mensajeErrorFormulario = "No pudimos guardar los cambios. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            if (generacionFormulario == _generacionFormulario)
                _guardando = false;
        }
    }

    private string? ObtenerError(string campo) => _erroresCampo.GetValueOrDefault(campo);

    private async Task CambiarResueltaAsync(Guid id, bool resuelta)
    {
        try
        {
            var resultado = await Mediator.Send(new MarcarResueltaIncidenciaCommand(id, resuelta));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                ToastService.Mostrar(resuelta ? "Incidencia marcada como resuelta." : "Incidencia reabierta.", TonoToast.Exito);
                await RecargarAsync();
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos actualizar el estado. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
    }

    private void AbrirEliminar(Guid id, string centroNombre)
    {
        _idAEliminar = id;
        _centroAEliminar = centroNombre;
        _confirmarEliminarVisible = true;
    }

    private async Task ConfirmarEliminarAsync()
    {
        _eliminando = true;

        try
        {
            var resultado = await Mediator.Send(new EliminarIncidenciaCommand(_idAEliminar));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                ToastService.Mostrar("Incidencia eliminada correctamente.", TonoToast.Exito);
                _confirmarEliminarVisible = false;
                await RecargarAsync();
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar la incidencia. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }

    private bool TodosSeleccionados =>
        _elementosPagina.Count > 0 && _elementosPagina.All(e => _seleccionados.Contains(e.Id));

    private void AlternarSeleccionTodos(bool marcar)
    {
        if (marcar)
            foreach (var elemento in _elementosPagina) _seleccionados.Add(elemento.Id);
        else
            _seleccionados.Clear();
    }

    private void AlternarSeleccion(Guid id, bool marcado)
    {
        if (marcado) _seleccionados.Add(id);
        else _seleccionados.Remove(id);
    }

    private async Task ConfirmarEliminarLoteAsync()
    {
        _eliminandoLote = true;

        try
        {
            var resultado = await Mediator.Send(new EliminarIncidenciasCommand(_seleccionados.ToList()));
            var dto = resultado.Valor;

            ToastService.Mostrar(
                dto.Errores.Count == 0
                    ? $"{dto.Eliminados} incidencia(s) eliminada(s)."
                    : $"{dto.Eliminados} eliminada(s). {dto.Errores.Count} no se pudieron borrar: {string.Join(" ", dto.Errores)}",
                dto.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);

            _seleccionados.Clear();
            _confirmarEliminarLoteVisible = false;
            await RecargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar las incidencias seleccionadas. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _eliminandoLote = false;
        }
    }

    private string ObtenerClaseFila(IncidenciaListaDto item) => item.Id == _idEnfocado ? "fila-enfocada" : "";

    private async Task ManejarAtajoAsync(string tecla)
    {
        if (_elementosPagina.Count == 0) return;

        switch (tecla)
        {
            case "j":
                {
                    var indiceActual = _idEnfocado is null ? -1 : _elementosPagina.FindIndex(e => e.Id == _idEnfocado);
                    _idEnfocado = _elementosPagina[Math.Min(indiceActual + 1, _elementosPagina.Count - 1)].Id;
                    break;
                }
            case "k":
                {
                    var indiceActual = _idEnfocado is null ? 0 : _elementosPagina.FindIndex(e => e.Id == _idEnfocado);
                    _idEnfocado = _elementosPagina[Math.Max(indiceActual - 1, 0)].Id;
                    break;
                }
            case "x":
                if (_idEnfocado is { } idAlternar)
                {
                    // Marcar con los checkboxes ocultos dejaba una selección
                    // invisible —con «Eliminar seleccionados» a la vista— sobre
                    // filas que no se ven marcadas: la trampa que
                    // AlternarSeleccionMultiple evita al apagarse.
                    _seleccionMultiple = true;
                    AlternarSeleccion(idAlternar, !_seleccionados.Contains(idAlternar));
                }
                break;
            case "Enter":
                if (_idEnfocado is { } idAbrir)
                {
                    var elemento = _elementosPagina.FirstOrDefault(e => e.Id == idAbrir);
                    if (elemento is not null)
                        await WorkspaceService.AbrirAsync(EntidadWorkspace.Centro, elemento.CentroId, elemento.CentroNombre, "informacion");
                }
                break;
        }

        StateHasChanged();
    }
}
