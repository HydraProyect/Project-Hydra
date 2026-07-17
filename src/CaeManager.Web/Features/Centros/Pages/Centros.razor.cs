using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Commands.EditarCentro;
using CaeManager.Application.Centros.Commands.EliminarCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.QuickGrid;

namespace CaeManager.Web.Features.Centros.Pages;

public partial class Centros : ComponentBase
{
    private readonly PaginationState _paginacion = new() { ItemsPerPage = 20 };
    private QuickGrid<CentroListaDto>? _grid;

    private string _busqueda = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    private IReadOnlyList<ClienteSelectorDto> _clientesDisponibles = [];
    private IReadOnlyList<EmpresaSelectorDto> _empresasDisponibles = [];

    private bool _drawerVisible;
    private Guid? _editandoId;
    private string _clienteId = string.Empty;
    private string _clienteNombreSoloLectura = string.Empty;
    private string _empresaId = string.Empty;
    private string _empresaNombreSoloLectura = string.Empty;
    private string _nombre = string.Empty;
    private string _codigoCentro = string.Empty;
    private string _direccion = string.Empty;
    private string _contacto = string.Empty;
    private string _contratoVigenteHasta = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _nombreAEliminar = string.Empty;
    private bool _eliminando;

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    protected override void OnInitialized()
    {
        if (!string.IsNullOrWhiteSpace(TerminoBusquedaInicial))
            _busqueda = TerminoBusquedaInicial;
    }

    private async ValueTask<GridItemsProviderResult<CentroListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<CentroListaDto> request)
    {
        _cargando = true;
        _errorCarga = false;

        try
        {
            var pagina = (request.StartIndex / _paginacion.ItemsPerPage) + 1;

            var resultado = await Mediator.Send(new ObtenerCentrosQuery(
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                ClienteId: null,
                Pagina: pagina,
                TamanoPagina: _paginacion.ItemsPerPage));

            _totalElementos = resultado.TotalElementos;

            return GridItemsProviderResult.From(resultado.Elementos.ToList(), resultado.TotalElementos);
        }
        catch (Exception)
        {
            _errorCarga = true;
            return GridItemsProviderResult.From(new List<CentroListaDto>(), 0);
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
        _clientesDisponibles = await Mediator.Send(new ObtenerClientesParaSelectorQuery());
        _empresasDisponibles = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery());

        _editandoId = null;
        _clienteId = string.Empty;
        _empresaId = string.Empty;
        _nombre = string.Empty;
        _codigoCentro = string.Empty;
        _direccion = string.Empty;
        _contacto = string.Empty;
        _contratoVigenteHasta = string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private async Task AbrirEditarAsync(Guid id)
    {
        var centro = await Mediator.Send(new ObtenerCentroPorIdQuery(id));
        if (centro is null)
        {
            ToastService.Mostrar("No encontramos este centro. Puede que ya se haya eliminado.", TonoToast.Error);
            await RecargarAsync();
            return;
        }

        _editandoId = centro.Id;
        _clienteId = centro.ClienteId.ToString();
        _clienteNombreSoloLectura = centro.ClienteRazonSocial;
        _empresaId = centro.EmpresaId.ToString();
        _empresaNombreSoloLectura = centro.EmpresaRazonSocial;
        _nombre = centro.Nombre;
        _codigoCentro = centro.CodigoCentro ?? string.Empty;
        _direccion = centro.Direccion ?? string.Empty;
        _contacto = centro.Contacto ?? string.Empty;
        _contratoVigenteHasta = centro.ContratoVigenteHasta?.ToString("yyyy-MM-dd") ?? string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        return Task.CompletedTask;
    }

    private async Task GuardarAsync()
    {
        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();

        try
        {
            var codigoCentro = string.IsNullOrWhiteSpace(_codigoCentro) ? null : _codigoCentro;
            var direccion = string.IsNullOrWhiteSpace(_direccion) ? null : _direccion;
            var contacto = string.IsNullOrWhiteSpace(_contacto) ? null : _contacto;
            var contratoVigenteHasta = DateOnly.TryParse(_contratoVigenteHasta, out var fecha) ? fecha : (DateOnly?)null;

            string? mensajeError;

            if (_editandoId is null)
            {
                if (!Guid.TryParse(_clienteId, out var clienteId))
                {
                    _mensajeErrorFormulario = "Selecciona un cliente.";
                    return;
                }

                if (!Guid.TryParse(_empresaId, out var empresaId))
                {
                    _mensajeErrorFormulario = "Selecciona una empresa.";
                    return;
                }

                var resultado = await Mediator.Send(
                    new CrearCentroCommand(clienteId, empresaId, _nombre, codigoCentro, direccion, contacto, contratoVigenteHasta));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }
            else
            {
                var resultado = await Mediator.Send(
                    new EditarCentroCommand(_editandoId.Value, _nombre, codigoCentro, direccion, contacto, contratoVigenteHasta));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }

            if (mensajeError is not null)
            {
                _mensajeErrorFormulario = mensajeError;
                return;
            }

            ToastService.Mostrar(
                _editandoId is null ? "Centro creado correctamente." : "Centro actualizado correctamente.",
                TonoToast.Exito);

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

    private void AbrirEliminar(Guid id, string nombre)
    {
        _idAEliminar = id;
        _nombreAEliminar = nombre;
        _confirmarEliminarVisible = true;
    }

    private async Task ConfirmarEliminarAsync()
    {
        _eliminando = true;

        try
        {
            var usuarioId = await CurrentUserService.ObtenerUsuarioActualIdAsync();
            var resultado = await Mediator.Send(new EliminarCentroCommand(_idAEliminar, usuarioId ?? Guid.Empty));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                ToastService.Mostrar("Centro eliminado correctamente.", TonoToast.Exito);
                _confirmarEliminarVisible = false;
                await RecargarAsync();
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar el centro. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }
}
