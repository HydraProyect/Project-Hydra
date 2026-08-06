using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Commands.EliminarEmpresa;
using CaeManager.Application.Empresas.Commands.EliminarEmpresas;
using CaeManager.Application.Empresas.Commands.RestaurarEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Web.Components;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.QuickGrid;

namespace CaeManager.Web.Features.Empresas.Pages;

public partial class Empresas : ComponentBase
{
    private readonly PaginationState _paginacion = new() { ItemsPerPage = 20 };
    private QuickGrid<EmpresaListaDto>? _grid;

    private string _busqueda = string.Empty;
    private string _estadoFiltro = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    private IReadOnlyList<ClienteSelectorDto> _clientesDisponibles = [];
    private IReadOnlyList<ElementoSeleccionable> _clientesDisponiblesSelector => _clientesDisponibles
        .Select(c => new ElementoSeleccionable(c.Id, c.RazonSocial))
        .ToList();

    private bool _drawerVisible;
    private string _razonSocial = string.Empty;
    private string _cif = string.Empty;
    private HashSet<Guid> _clienteIdsSeleccionados = [];
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _razonSocialAEliminar = string.Empty;
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
    private List<EmpresaListaDto> _elementosPagina = [];
    private Guid? _idEnfocado;
    private bool _eliminandoLote;
    private bool _confirmarEliminarLoteVisible;

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    /// <summary>
    /// Filtro de estado documental (ver ICalculoEstadoDocumentalService) — esta
    /// entidad no tiene estado propio en el modelo, se deriva de sus Documentos.
    /// </summary>
    [SupplyParameterFromQuery(Name = "estado")]
    public string? EstadoInicial { get; set; }

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    /// <summary>Comando del palette "Crear empresa «nombre»" (P3-31): abre el Drawer con la razón social precargada.</summary>
    [SupplyParameterFromQuery] public string? Accion { get; set; }
    [SupplyParameterFromQuery] public string? Nombre { get; set; }

    /// <summary>
    /// Encadenado desde "Continuar con la empresa" en /clientes (Fase A2): el
    /// Cliente recién creado llega premarcado en el selector, para no tener
    /// que volver a buscarlo.
    /// </summary>
    [SupplyParameterFromQuery] public Guid? ClienteId { get; set; }

    private GridItemsProvider<EmpresaListaDto>? _proveedorElementos;

    protected override async Task OnInitializedAsync()
    {
        // Delegado estable — ver Clientes.razor.cs (bucle de recargas de QuickGrid).
        _proveedorElementos = ProveerElementosAsync;

        if (Accion == "crear")
        {
            await AbrirCrear();
            if (!string.IsNullOrWhiteSpace(Nombre))
                _razonSocial = Nombre;
            if (ClienteId is not null && _clientesDisponibles.Any(c => c.Id == ClienteId))
                _clienteIdsSeleccionados = [ClienteId.Value];
        }
    }

    /// <summary>
    /// Se re-ejecuta en cada navegación dentro de la propia página (recargar,
    /// compartir la URL, volver atrás) — no solo en el primer render — para
    /// que el filtro de la URL sea la fuente de verdad, no solo su semilla
    /// inicial (P1-18 de docs/business/MATURITY_REVIEW.md).
    /// </summary>
    protected override void OnParametersSet()
    {
        var deLaUrl = TerminoBusquedaInicial ?? string.Empty;
        if (deLaUrl != _busqueda)
            _busqueda = deLaUrl;

        var estadoDeLaUrl = EstadoDocumentoUi.OpcionesDocumentales.Any(o => o.Valor == EstadoInicial)
            ? EstadoInicial!
            : string.Empty;
        if (estadoDeLaUrl != _estadoFiltro)
            _estadoFiltro = estadoDeLaUrl;
    }

    private async Task CambiarEstadoAsync(string valor)
    {
        _estadoFiltro = valor;
        NavigationManager.ActualizarFiltroEnUrl("estado", valor);
        await RecargarAsync();
    }

    private async ValueTask<GridItemsProviderResult<EmpresaListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<EmpresaListaDto> request)
    {
        _cargando = true;
        _errorCarga = false;

        try
        {
            var pagina = (request.StartIndex / _paginacion.ItemsPerPage) + 1;
            var (ordenarPor, descendente) = LecturaOrden.Leer(request);

            var resultado = await Mediator.Send(new ObtenerEmpresasQuery(
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                Pagina: pagina,
                TamanoPagina: _paginacion.ItemsPerPage,
                OrdenarPor: ordenarPor,
                Descendente: descendente,
                EstadoDocumental: string.IsNullOrWhiteSpace(_estadoFiltro) ? null : _estadoFiltro));

            _totalElementos = resultado.TotalElementos;

            var elementos = resultado.Elementos.ToList();
            _elementosPagina = elementos;
            _seleccionados.Clear();
            _idEnfocado = null;

            return GridItemsProviderResult.From(elementos, resultado.TotalElementos);
        }
        catch (Exception)
        {
            _errorCarga = true;
            return GridItemsProviderResult.From(new List<EmpresaListaDto>(), 0);
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
        NavigationManager.ActualizarFiltroEnUrl("q", valor);
        await RecargarAsync();
    }

    private async Task RecargarAsync()
    {
        await _paginacion.SetCurrentPageIndexAsync(0);

        if (_grid is not null)
            await _grid.RefreshDataAsync();

        StateHasChanged();
    }

    private async Task AbrirCrear()
    {
        _clientesDisponibles = await Mediator.Send(new ObtenerClientesParaSelectorQuery());

        _razonSocial = string.Empty;
        _cif = string.Empty;
        _clienteIdsSeleccionados = [];
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private void AlternarCliente(Guid clienteId, bool seleccionado)
    {
        if (seleccionado)
            _clienteIdsSeleccionados.Add(clienteId);
        else
            _clienteIdsSeleccionados.Remove(clienteId);
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        return Task.CompletedTask;
    }

    private Task GuardarAsync() => GuardarAsync(continuarACrearCentro: false);

    /// <summary>
    /// "Continuar con el centro" (Fase A2): al crear una Empresa nueva con
    /// éxito, navega directamente a <c>/centros?accion=crear</c> con Cliente
    /// y Empresa ya fijados, en vez de solo cerrar el Drawer.
    /// </summary>
    private Task GuardarYCrearCentroAsync() => GuardarAsync(continuarACrearCentro: true);

    private async Task GuardarAsync(bool continuarACrearCentro)
    {
        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();

        try
        {
            var clienteIds = _clienteIdsSeleccionados.ToList();
            var cif = string.IsNullOrWhiteSpace(_cif) ? null : _cif;

            var resultado = await Mediator.Send(new CrearEmpresaCommand(_razonSocial, cif, clienteIds));
            if (resultado.EsFallido)
            {
                _mensajeErrorFormulario = resultado.Error.Mensaje;
                return;
            }

            var empresaCreadaId = resultado.Valor;
            ToastService.Mostrar("Empresa creada correctamente.", TonoToast.Exito);

            if (continuarACrearCentro)
            {
                // Prioridad: el Cliente que trajo la cadena (si sigue
                // marcado) · si no, el único Cliente marcado en el selector ·
                // si hay varios o ninguno, no hay uno solo que prefijar.
                var clienteParaCentro = ClienteId is not null && clienteIds.Contains(ClienteId.Value)
                    ? ClienteId
                    : clienteIds.Count == 1 ? clienteIds[0] : (Guid?)null;

                var destino = clienteParaCentro is null
                    ? $"/centros?accion=crear&empresaId={empresaCreadaId}"
                    : $"/centros?accion=crear&clienteId={clienteParaCentro}&empresaId={empresaCreadaId}";
                NavigationManager.NavigateTo(destino);
                return;
            }

            _drawerVisible = false;
            await RecargarAsync();
        }
        catch (ValidationException ex)
        {
            _erroresCampo = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.First().ErrorMessage);
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

    private string? ObtenerError(string campo) => _erroresCampo.GetValueOrDefault(campo);

    private void AbrirEliminar(Guid id, string razonSocial)
    {
        _idAEliminar = id;
        _razonSocialAEliminar = razonSocial;
        _confirmarEliminarVisible = true;
    }

    private async Task ConfirmarEliminarAsync()
    {
        _eliminando = true;

        try
        {
            var usuarioId = await CurrentUserService.ObtenerUsuarioActualIdAsync();
            var resultado = await Mediator.Send(new EliminarEmpresaCommand(_idAEliminar, usuarioId ?? Guid.Empty));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                var idEliminado = _idAEliminar;
                ToastService.Mostrar("Empresa eliminada correctamente.", TonoToast.Exito, "Deshacer", () => DeshacerEliminarAsync(idEliminado));
                _confirmarEliminarVisible = false;
                await RecargarAsync();
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar la empresa. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }

    /// <summary>Fase D ("Deshacer al eliminar") — acción del toast tras eliminar, ver RestaurarEmpresaCommand.</summary>
    private async Task DeshacerEliminarAsync(Guid id)
    {
        var resultado = await Mediator.Send(new RestaurarEmpresaCommand(id));

        ToastService.Mostrar(
            resultado.EsExitoso ? "Empresa restaurada." : resultado.Error.Mensaje,
            resultado.EsExitoso ? TonoToast.Exito : TonoToast.Error);

        if (resultado.EsExitoso)
            await RecargarAsync();
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
            var usuarioId = await CurrentUserService.ObtenerUsuarioActualIdAsync();
            var resultado = await Mediator.Send(new EliminarEmpresasCommand(_seleccionados.ToList(), usuarioId ?? Guid.Empty));
            var dto = resultado.Valor;

            ToastService.Mostrar(
                dto.Errores.Count == 0
                    ? $"{dto.Eliminados} empresa(s) eliminada(s)."
                    : $"{dto.Eliminados} eliminada(s). {dto.Errores.Count} no se pudieron borrar: {string.Join(" ", dto.Errores)}",
                dto.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);

            _seleccionados.Clear();
            _confirmarEliminarLoteVisible = false;
            await RecargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar las empresas seleccionadas. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _eliminandoLote = false;
        }
    }

    private string ObtenerClaseFila(EmpresaListaDto item) => item.Id == _idEnfocado ? "fila-enfocada" : "";

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
                    AlternarSeleccion(idAlternar, !_seleccionados.Contains(idAlternar));
                break;
            case "Enter":
                if (_idEnfocado is { } idAbrir)
                {
                    var elemento = _elementosPagina.FirstOrDefault(e => e.Id == idAbrir);
                    if (elemento is not null)
                        await WorkspaceService.AbrirAsync(EntidadWorkspace.Empresa, elemento.Id, elemento.RazonSocial, "informacion");
                }
                break;
        }

        StateHasChanged();
    }
}
