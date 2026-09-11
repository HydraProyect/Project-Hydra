using CaeManager.Web.Components;
using CaeManager.Application.Gestiones.Commands.CompletarGestion;
using CaeManager.Application.Gestiones.Commands.EliminarGestion;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Domain.Gestiones;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.QuickGrid;

namespace CaeManager.Web.Features.Gestiones.Pages;

public partial class Gestiones : ComponentBase
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

    private QuickGrid<GestionListaDto>? _grid;

    private string _busqueda = string.Empty;
    private string _filtroEstado = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    /// <summary>
    /// Número de la última carga pedida. Cada carga captura el suyo al empezar
    /// y, al volver del <c>await</c>, solo escribe estado si sigue siendo la
    /// vigente: si mientras tanto cambió el filtro, la búsqueda, la página o el
    /// orden, su respuesta es de otra pregunta. QuickGrid ya descarta las
    /// FILAS de una carga superada, pero no sabe nada de
    /// <see cref="_totalElementos"/> ni de <see cref="_errorCarga"/>, que son
    /// de esta página: sin esto, una respuesta lenta del filtro anterior
    /// pisaba el total del nuevo y el estado vacío desaparecía dejando una
    /// rejilla sin filas.
    /// </summary>
    private int _cargaVigente;

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private bool _eliminando;

    /// <summary>
    /// Fila abierta en la vista rápida. Es la propia fila de la lista: no hay
    /// consulta por Id de Gestion, y así el panel no puede discrepar de ella.
    /// </summary>
    private GestionListaDto? _vistaRapida;

    /// <summary>
    /// Gestiones cuyo cambio de estado está viajando. Guarda de reentrada POR
    /// GESTIÓN: pulsar otra vez sobre la misma (el botón de la vista rápida y
    /// el del menú de su fila son dos disparadores de lo mismo) no manda un
    /// segundo comando; pulsar sobre OTRA sí sigue funcionando — una bandera
    /// única lo habría descartado sin decir nada.
    /// </summary>
    private readonly HashSet<Guid> _cambiandoEstado = [];

    private GridItemsProvider<GestionListaDto>? _proveedorElementos;

    private static readonly IReadOnlyList<OpcionEstado> OpcionesEstado =
    [
        new(nameof(EstadoGestion.Pendiente), "Pendientes"),
        new(nameof(EstadoGestion.Completada), "Completadas")
    ];

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "estado")]
    public string? EstadoInicial { get; set; }

    protected override void OnInitialized() => _proveedorElementos = ProveerElementosAsync;

    /// <summary>La URL es la fuente de verdad del filtro (P1-18) — ver el resto de listados.</summary>
    protected override void OnParametersSet()
    {
        var deLaUrl = Enum.TryParse<EstadoGestion>(EstadoInicial, out _) ? EstadoInicial! : string.Empty;
        if (deLaUrl != _filtroEstado)
            _filtroEstado = deLaUrl;
    }

    private async ValueTask<GridItemsProviderResult<GestionListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<GestionListaDto> request)
    {
        // Todo lo que define la pregunta se lee ANTES del await.
        var carga = ++_cargaVigente;
        var (ordenarPor, descendente) = LecturaOrden.Leer(request);
        var consulta = new ObtenerGestionesQuery(
            Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
            Estado: Enum.TryParse<EstadoGestion>(_filtroEstado, out var estado) ? estado : null,
            TrabajadorId: null,
            Pagina: (request.StartIndex / _paginacion.ItemsPerPage) + 1,
            TamanoPagina: _paginacion.ItemsPerPage,
            OrdenarPor: ordenarPor,
            Descendente: descendente);

        _cargando = true;
        _errorCarga = false;

        try
        {
            var resultado = await Mediator.Send(consulta, request.CancellationToken);

            if (carga != _cargaVigente)
                return GridItemsProviderResult.From(new List<GestionListaDto>(), 0);

            _totalElementos = resultado.TotalElementos;

            return GridItemsProviderResult.From(resultado.Elementos.ToList(), resultado.TotalElementos);
        }
        catch (Exception) when (carga != _cargaVigente)
        {
            // Una carga superada que falla (o que QuickGrid canceló) no es un
            // error de la vigente: no puede tapar su resultado.
            return GridItemsProviderResult.From(new List<GestionListaDto>(), 0);
        }
        catch (Exception)
        {
            _errorCarga = true;
            return GridItemsProviderResult.From(new List<GestionListaDto>(), 0);
        }
        finally
        {
            if (carga == _cargaVigente)
            {
                _cargando = false;
                StateHasChanged();
            }
        }
    }

    private async Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        await RecargarAsync();
    }

    private async Task FiltrarPorEstadoAsync(string valor)
    {
        _filtroEstado = valor;
        NavigationManager.ActualizarFiltroEnUrl("estado", valor);
        await RecargarAsync();
    }

    /// <summary>
    /// Los dos filtros de la barra. Separa "sin gestiones" de "ninguna con
    /// estos filtros": el texto sin filtrar explica cómo se generan las
    /// gestiones, y con un estado puesto se lee como que el mecanismo falla.
    /// </summary>
    private bool HayFiltrosActivos =>
        !string.IsNullOrWhiteSpace(_busqueda) || !string.IsNullOrWhiteSpace(_filtroEstado);

    private string TextoChipEstado => _filtroEstado == nameof(EstadoGestion.Completada)
        ? "Estado: completadas"
        : "Estado: pendientes";

    /// <summary>
    /// Cuántas coinciden. Sin filtros no habla de ninguno; con filtros dice que
    /// el número es el de las que coinciden, no el de la cartera.
    /// </summary>
    private string TextoConteo
    {
        get
        {
            var sustantivo = _totalElementos == 1 ? "gestión" : "gestiones";
            return HayFiltrosActivos
                ? $"{_totalElementos} {sustantivo} con estos filtros"
                : $"{_totalElementos} {sustantivo}";
        }
    }

    /// <summary>
    /// Quita los dos filtros en una sola recarga. El estado vive además en la
    /// URL y se limpia allí: <see cref="OnParametersSet"/> re-sincroniza desde
    /// ella en cada navegación dentro de la página.
    /// </summary>
    private async Task LimpiarFiltrosAsync()
    {
        _busqueda = string.Empty;
        _filtroEstado = string.Empty;
        NavigationManager.ActualizarFiltroEnUrl("estado", string.Empty);
        await RecargarAsync();
    }

    private async Task QuitarBusquedaAsync()
    {
        _busqueda = string.Empty;
        await RecargarAsync();
    }

    /// <summary>Mismo motivo que <see cref="LimpiarFiltrosAsync"/>: el estado se quita también de la URL.</summary>
    private async Task QuitarFiltroEstadoAsync()
    {
        _filtroEstado = string.Empty;
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

    private static TonoBadge TonoEstado(EstadoGestion estado) =>
        estado == EstadoGestion.Completada ? TonoBadge.Exito : TonoBadge.Advertencia;

    private static string TextoEstado(EstadoGestion estado) =>
        estado == EstadoGestion.Completada ? "Completada" : "Pendiente";

    private string ObtenerClaseFila(GestionListaDto fila) =>
        fila.Id == _vistaRapida?.Id ? "fila-enfocada" : string.Empty;

    private void AbrirVistaRapida(GestionListaDto fila) => _vistaRapida = fila;

    private void CerrarVistaRapida() => _vistaRapida = null;

    /// <summary>
    /// Los 360 se abren desde la vista rápida: se cierra primero para no dejar
    /// dos paneles laterales apilados sobre la lista.
    /// </summary>
    private Task AbrirWorkspaceAsync(EntidadWorkspace tipo, Guid id, string titulo, string pestana)
    {
        _vistaRapida = null;
        return WorkspaceService.AbrirAsync(tipo, id, titulo, pestana);
    }

    private async Task CambiarEstadoAsync(Guid id, bool completada)
    {
        if (!_cambiandoEstado.Add(id))
            return;

        try
        {
            var resultado = await Mediator.Send(new CompletarGestionCommand(id, completada));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            // Solo si la vista rápida sigue enseñando ESA gestión: mientras el
            // comando viajaba se pudo abrir otra fila, y a esa no le ha pasado nada.
            if (_vistaRapida is { } vista && vista.Id == id)
                _vistaRapida = vista with { Estado = completada ? EstadoGestion.Completada : EstadoGestion.Pendiente };

            await RecargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos actualizar el estado. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _cambiandoEstado.Remove(id);
        }
    }

    private void AbrirEliminar(Guid id)
    {
        _idAEliminar = id;
        _confirmarEliminarVisible = true;
    }

    private async Task ConfirmarEliminarAsync()
    {
        _eliminando = true;
        var id = _idAEliminar;

        try
        {
            var resultado = await Mediator.Send(new EliminarGestionCommand(id));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                ToastService.Mostrar("Gestión eliminada correctamente.", TonoToast.Exito);
                _confirmarEliminarVisible = false;

                // Una vista rápida abierta sobre la gestión borrada enseñaría,
                // con sus botones activos, algo que ya no está en la lista.
                if (_vistaRapida?.Id == id)
                    _vistaRapida = null;

                await RecargarAsync();
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar la gestión. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }
}
