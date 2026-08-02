using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Commands.EditarEmpresa;
using CaeManager.Application.Empresas.Commands.EliminarEmpresa;
using CaeManager.Application.Empresas.Commands.GuardarCredencialAccesoEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerCredencialAccesoEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Web.Components;
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
    private IReadOnlyList<ElementoSeleccionable> _clientesDisponiblesSelector => _clientesDisponibles
        .Select(c => new ElementoSeleccionable(c.Id, c.RazonSocial))
        .ToList();

    private bool _drawerVisible;
    private Guid? _editandoId;
    // Version del registro tal como se abrio: vuelve en el Command para
    // detectar que otra persona guardo mientras el formulario estaba abierto.
    private Guid _versionEditando;
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

    private string _credencialUrl = string.Empty;
    private string _credencialCampoEmpresa = string.Empty;
    private string _credencialUsuario = string.Empty;
    private string _credencialContrasena = string.Empty;
    private string _credencialNotas = string.Empty;
    private bool _guardandoCredenciales;
    private string? _mensajeErrorCredenciales;

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private GridItemsProvider<EmpresaListaDto>? _proveedorElementos;

    protected override void OnInitialized()
    {
        // Delegado estable — ver Clientes.razor.cs (bucle de recargas de QuickGrid).
        _proveedorElementos = ProveerElementosAsync;
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

        _editandoId = null;
        _razonSocial = string.Empty;
        _cif = string.Empty;
        _clienteIdsSeleccionados = [];
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _credencialUrl = string.Empty;
        _credencialCampoEmpresa = string.Empty;
        _credencialUsuario = string.Empty;
        _credencialContrasena = string.Empty;
        _credencialNotas = string.Empty;
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
        _versionEditando = empresa.Version;
        _razonSocial = empresa.RazonSocial;
        _cif = empresa.Cif ?? string.Empty;
        _clienteIdsSeleccionados = empresa.ClienteIds.ToHashSet();
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;

        var credencial = await Mediator.Send(new ObtenerCredencialAccesoEmpresaQuery(empresa.Id));
        _credencialUrl = credencial?.UrlAcceso ?? string.Empty;
        _credencialCampoEmpresa = credencial?.CampoEmpresa ?? string.Empty;
        _credencialUsuario = credencial?.Usuario ?? string.Empty;
        _credencialContrasena = credencial?.Contrasena ?? string.Empty;
        _credencialNotas = credencial?.Notas ?? string.Empty;
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
            var notas = string.IsNullOrWhiteSpace(_credencialNotas) ? null : _credencialNotas;

            var resultado = await Mediator.Send(
                new GuardarCredencialAccesoEmpresaCommand(_editandoId.Value, urlAcceso, campoEmpresa, usuario, contrasena, notas));

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
            var clienteIds = _clienteIdsSeleccionados.ToList();
            var cif = string.IsNullOrWhiteSpace(_cif) ? null : _cif;
            var eraCreacion = _editandoId is null;

            if (eraCreacion)
            {
                var resultado = await Mediator.Send(new CrearEmpresaCommand(_razonSocial, cif, clienteIds));
                if (resultado.EsFallido)
                {
                    _mensajeErrorFormulario = resultado.Error.Mensaje;
                    return;
                }

                // Tras crear, el drawer no se cierra — pasa a modo edición
                // para que las credenciales de acceso queden visibles sin
                // tener que reabrir el formulario desde la tabla.
                _editandoId = resultado.Valor;
            }
            else
            {
                var resultado = await Mediator.Send(new EditarEmpresaCommand(_editandoId!.Value, _razonSocial, cif, clienteIds, _versionEditando));
                if (resultado.EsFallido)
                {
                    _mensajeErrorFormulario = resultado.Error.Mensaje;
                    return;
                }
            }

            ToastService.Mostrar(
                eraCreacion ? "Empresa creada correctamente." : "Empresa actualizada correctamente.",
                TonoToast.Exito);

            if (!eraCreacion)
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
