using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Commands.EditarEmpresa;
using CaeManager.Application.Empresas.Commands.EliminarEmpresa;
using CaeManager.Application.Empresas.Commands.GuardarCredencialAccesoEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerCredencialAccesoEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.QuickGrid;

namespace CaeManager.Web.Features.Empresas.Pages;

public partial class Empresas : ComponentBase
{
    private readonly PaginationState _paginacion = new() { ItemsPerPage = 20 };
    private QuickGrid<EmpresaListaDto>? _grid;

    private string _busqueda = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    private IReadOnlyList<ClienteSelectorDto> _clientesDisponibles = [];

    private bool _drawerVisible;
    private Guid? _editandoId;
    private string _razonSocial = string.Empty;
    private HashSet<Guid> _clienteIdsSeleccionados = [];
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _razonSocialAEliminar = string.Empty;
    private bool _eliminando;

    private string _credencialUrl = string.Empty;
    private string _credencialCampoEmpresa = string.Empty;
    private string _credencialUsuario = string.Empty;
    private string _credencialContrasena = string.Empty;
    private bool _guardandoCredenciales;
    private string? _mensajeErrorCredenciales;

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    protected override void OnInitialized()
    {
        if (!string.IsNullOrWhiteSpace(TerminoBusquedaInicial))
            _busqueda = TerminoBusquedaInicial;
    }

    private async ValueTask<GridItemsProviderResult<EmpresaListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<EmpresaListaDto> request)
    {
        _cargando = true;
        _errorCarga = false;

        try
        {
            var pagina = (request.StartIndex / _paginacion.ItemsPerPage) + 1;

            var resultado = await Mediator.Send(new ObtenerEmpresasQuery(
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                Pagina: pagina,
                TamanoPagina: _paginacion.ItemsPerPage));

            _totalElementos = resultado.TotalElementos;

            return GridItemsProviderResult.From(resultado.Elementos.ToList(), resultado.TotalElementos);
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

        _editandoId = null;
        _razonSocial = string.Empty;
        _clienteIdsSeleccionados = [];
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _credencialUrl = string.Empty;
        _credencialCampoEmpresa = string.Empty;
        _credencialUsuario = string.Empty;
        _credencialContrasena = string.Empty;
        _mensajeErrorCredenciales = null;
        _drawerVisible = true;
    }

    private async Task AbrirEditarAsync(Guid id)
    {
        _clientesDisponibles = await Mediator.Send(new ObtenerClientesParaSelectorQuery());

        var empresa = await Mediator.Send(new ObtenerEmpresaPorIdQuery(id));
        if (empresa is null)
        {
            ToastService.Mostrar("No encontramos esta empresa. Puede que ya se haya eliminado.", TonoToast.Error);
            await RecargarAsync();
            return;
        }

        _editandoId = empresa.Id;
        _razonSocial = empresa.RazonSocial;
        _clienteIdsSeleccionados = empresa.ClienteIds.ToHashSet();
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;

        var credencial = await Mediator.Send(new ObtenerCredencialAccesoEmpresaQuery(empresa.Id));
        _credencialUrl = credencial?.UrlAcceso ?? string.Empty;
        _credencialCampoEmpresa = credencial?.CampoEmpresa ?? string.Empty;
        _credencialUsuario = credencial?.Usuario ?? string.Empty;
        _credencialContrasena = credencial?.Contrasena ?? string.Empty;
        _mensajeErrorCredenciales = null;

        _drawerVisible = true;
    }

    private async Task GuardarCredencialesAsync()
    {
        if (_editandoId is null) return;

        _guardandoCredenciales = true;
        _mensajeErrorCredenciales = null;

        try
        {
            var urlAcceso = string.IsNullOrWhiteSpace(_credencialUrl) ? null : _credencialUrl;
            var campoEmpresa = string.IsNullOrWhiteSpace(_credencialCampoEmpresa) ? null : _credencialCampoEmpresa;
            var usuario = string.IsNullOrWhiteSpace(_credencialUsuario) ? null : _credencialUsuario;
            var contrasena = string.IsNullOrWhiteSpace(_credencialContrasena) ? null : _credencialContrasena;

            var resultado = await Mediator.Send(
                new GuardarCredencialAccesoEmpresaCommand(_editandoId.Value, urlAcceso, campoEmpresa, usuario, contrasena));

            if (resultado.EsFallido)
            {
                _mensajeErrorCredenciales = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Credenciales guardadas correctamente.", TonoToast.Exito);
        }
        catch (Exception)
        {
            _mensajeErrorCredenciales = "No pudimos guardar las credenciales. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardandoCredenciales = false;
        }
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

    private async Task GuardarAsync()
    {
        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();

        try
        {
            string? mensajeError;

            var clienteIds = _clienteIdsSeleccionados.ToList();

            if (_editandoId is null)
            {
                var resultado = await Mediator.Send(new CrearEmpresaCommand(_razonSocial, clienteIds));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }
            else
            {
                var resultado = await Mediator.Send(new EditarEmpresaCommand(_editandoId.Value, _razonSocial, clienteIds));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }

            if (mensajeError is not null)
            {
                _mensajeErrorFormulario = mensajeError;
                return;
            }

            ToastService.Mostrar(
                _editandoId is null ? "Empresa creada correctamente." : "Empresa actualizada correctamente.",
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
                ToastService.Mostrar("Empresa eliminada correctamente.", TonoToast.Exito);
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
}
