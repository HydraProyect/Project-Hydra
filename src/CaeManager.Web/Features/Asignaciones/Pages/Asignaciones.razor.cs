using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Asignaciones.Commands.DarDeBajaAsignacion;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignaciones;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
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
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresDisponibles = [];
    private IReadOnlyList<CentroSelectorDto> _centrosDisponibles = [];

    private bool _drawerVisible;
    private string _trabajadorId = string.Empty;
    private string _centroId = string.Empty;
    private string _fechaAlta = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;

    private bool _darDeBajaVisible;
    private Guid _idParaBaja;
    private string _trabajadorParaBaja = string.Empty;
    private string _centroParaBaja = string.Empty;
    private string _fechaBaja = string.Empty;
    private bool _procesandoBaja;

    private GridItemsProvider<AsignacionListaDto>? _proveedorElementos;

    // Delegado estable — ver Clientes.razor.cs (bucle de recargas de QuickGrid).
    protected override void OnInitialized() => _proveedorElementos = ProveerElementosAsync;

    private async ValueTask<GridItemsProviderResult<AsignacionListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<AsignacionListaDto> request)
    {
        _cargando = true;
        _errorCarga = false;

        try
        {
            var pagina = (request.StartIndex / _paginacion.ItemsPerPage) + 1;

            var resultado = await Mediator.Send(new ObtenerAsignacionesQuery(
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                Pagina: pagina,
                TamanoPagina: _paginacion.ItemsPerPage));

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

        _trabajadorId = string.Empty;
        _centroId = string.Empty;
        _fechaAlta = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private async Task GuardarAsync()
    {
        _guardando = true;
        _mensajeErrorFormulario = null;

        try
        {
            if (!Guid.TryParse(_trabajadorId, out var trabajadorId))
            {
                _mensajeErrorFormulario = "Selecciona un trabajador.";
                return;
            }

            if (!Guid.TryParse(_centroId, out var centroId))
            {
                _mensajeErrorFormulario = "Selecciona un centro.";
                return;
            }

            if (!DateOnly.TryParse(_fechaAlta, out var fechaAlta))
            {
                _mensajeErrorFormulario = "Introduce una fecha de alta válida.";
                return;
            }

            var resultado = await Mediator.Send(new CrearAsignacionCommand(trabajadorId, centroId, fechaAlta));

            if (resultado.EsFallido)
            {
                _mensajeErrorFormulario = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Asignación creada correctamente.", TonoToast.Exito);
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
