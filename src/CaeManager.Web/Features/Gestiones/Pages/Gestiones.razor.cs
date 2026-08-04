using CaeManager.Web.Components;
using CaeManager.Application.Gestiones.Commands.CompletarGestion;
using CaeManager.Application.Gestiones.Commands.EliminarGestion;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Domain.Gestiones;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.QuickGrid;

namespace CaeManager.Web.Features.Gestiones.Pages;

public partial class Gestiones : ComponentBase
{
    private readonly PaginationState _paginacion = new() { ItemsPerPage = 20 };
    private QuickGrid<GestionListaDto>? _grid;

    private string _busqueda = string.Empty;
    private string _filtroEstado = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private bool _eliminando;

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
        _cargando = true;
        _errorCarga = false;

        try
        {
            var pagina = (request.StartIndex / _paginacion.ItemsPerPage) + 1;

            var (ordenarPor, descendente) = LecturaOrden.Leer(request);

            var resultado = await Mediator.Send(new ObtenerGestionesQuery(
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                Estado: Enum.TryParse<EstadoGestion>(_filtroEstado, out var estado) ? estado : null,
                TrabajadorId: null,
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
            return GridItemsProviderResult.From(new List<GestionListaDto>(), 0);
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

    private async Task FiltrarPorEstadoAsync(string valor)
    {
        _filtroEstado = valor;
        NavigationManager.ActualizarFiltroEnUrl("estado", valor);
        await RecargarAsync();
    }

    private async Task RecargarAsync()
    {
        await _paginacion.SetCurrentPageIndexAsync(0);

        if (_grid is not null)
            await _grid.RefreshDataAsync();

        StateHasChanged();
    }

    private async Task CambiarEstadoAsync(Guid id, bool completada)
    {
        try
        {
            var resultado = await Mediator.Send(new CompletarGestionCommand(id, completada));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            await RecargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos actualizar el estado. Intenta nuevamente en unos segundos.", TonoToast.Error);
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

        try
        {
            var usuarioId = await CurrentUserService.ObtenerUsuarioActualIdAsync();
            var resultado = await Mediator.Send(new EliminarGestionCommand(_idAEliminar, usuarioId ?? Guid.Empty));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                ToastService.Mostrar("Gestión eliminada correctamente.", TonoToast.Exito);
                _confirmarEliminarVisible = false;
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
