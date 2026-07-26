using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Clientes.Commands.EditarCliente;
using CaeManager.Application.Clientes.Commands.EliminarCliente;
using CaeManager.Application.Clientes.Commands.ReasignarEjecutivoCliente;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.QuickGrid;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Features.Clientes.Pages;

public record GestorCaeSelectorDto(Guid Id, string NombreCompleto, string Email);

public partial class Clientes : ComponentBase
{
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;

    private static readonly string[] RolesQuePuedenReasignar =
        [Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae];

    private readonly PaginationState _paginacion = new() { ItemsPerPage = 20 };
    private QuickGrid<ClienteListaDto>? _grid;

    private bool _puedeReasignarEjecutivo;
    private IReadOnlyList<GestorCaeSelectorDto> _gestoresDisponibles = [];
    private string _ejecutivoUsuarioId = string.Empty;
    private string _ejecutivoUsuarioIdOriginal = string.Empty;

    private string _busqueda = string.Empty;
    private bool _soloCriticos;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    private bool _drawerVisible;
    private Guid? _editandoId;
    private string _razonSocial = string.Empty;
    private string _cif = string.Empty;
    private bool _esCritico;
    private string _notas = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _razonSocialAEliminar = string.Empty;
    private bool _eliminando;

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    protected override async Task OnInitializedAsync()
    {
        // Permite que el buscador global (Ctrl/Cmd+K) navegue aquí con el
        // filtro ya cargado, p. ej. /clientes?q=Cadena+Industrial.
        if (!string.IsNullOrWhiteSpace(TerminoBusquedaInicial))
            _busqueda = TerminoBusquedaInicial;

        // Reasignar el Gestor CAE dueño de un Cliente es una decisión de
        // rango superior (ver ReasignarEjecutivoClienteCommand) — el propio
        // Gestor CAE no ve ni puede tocar este campo sobre sus propios
        // clientes.
        var estadoAutenticacion = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        _puedeReasignarEjecutivo = RolesQuePuedenReasignar.Any(estadoAutenticacion.User.IsInRole);
    }

    private async ValueTask<GridItemsProviderResult<ClienteListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<ClienteListaDto> request)
    {
        _cargando = true;
        _errorCarga = false;

        try
        {
            var pagina = (request.StartIndex / _paginacion.ItemsPerPage) + 1;

            var resultado = await Mediator.Send(new ObtenerClientesQuery(
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                SoloCriticos: _soloCriticos ? true : null,
                Pagina: pagina,
                TamanoPagina: _paginacion.ItemsPerPage));

            _totalElementos = resultado.TotalElementos;

            return GridItemsProviderResult.From(resultado.Elementos.ToList(), resultado.TotalElementos);
        }
        catch (Exception)
        {
            _errorCarga = true;
            return GridItemsProviderResult.From(new List<ClienteListaDto>(), 0);
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
        // SetCurrentPageIndexAsync no dispara una recarga si el índice no cambia
        // (p.ej. ya estábamos en la página 0), así que se refresca explícitamente.
        await _paginacion.SetCurrentPageIndexAsync(0);

        if (_grid is not null)
            await _grid.RefreshDataAsync();

        StateHasChanged();
    }

    private void AbrirCrear()
    {
        _editandoId = null;
        _razonSocial = string.Empty;
        _cif = string.Empty;
        _esCritico = false;
        _notas = string.Empty;
        _ejecutivoUsuarioId = string.Empty;
        _ejecutivoUsuarioIdOriginal = string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private async Task AbrirEditarAsync(Guid id)
    {
        var cliente = await Mediator.Send(new ObtenerClientePorIdQuery(id));
        if (cliente is null)
        {
            ToastService.Mostrar("No encontramos este cliente. Puede que ya se haya eliminado.", TonoToast.Error);
            await RecargarAsync();
            return;
        }

        if (_puedeReasignarEjecutivo && _gestoresDisponibles.Count == 0)
        {
            var gestores = await UserManager.GetUsersInRoleAsync(Roles.GestorCae);
            _gestoresDisponibles = gestores
                .OrderBy(u => u.NombreCompleto)
                .Select(u => new GestorCaeSelectorDto(u.Id, u.NombreCompleto, u.Email ?? string.Empty))
                .ToList();
        }

        _editandoId = cliente.Id;
        _razonSocial = cliente.RazonSocial;
        _cif = cliente.Cif;
        _esCritico = cliente.EsCritico;
        _ejecutivoUsuarioId = cliente.EjecutivoUsuarioId?.ToString() ?? string.Empty;
        _ejecutivoUsuarioIdOriginal = _ejecutivoUsuarioId;
        _notas = cliente.Notas ?? string.Empty;
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
            var notas = string.IsNullOrWhiteSpace(_notas) ? null : _notas;
            string? mensajeError;

            if (_editandoId is null)
            {
                var resultado = await Mediator.Send(new CrearClienteCommand(_razonSocial, _cif, _esCritico, notas));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }
            else
            {
                var resultado = await Mediator.Send(new EditarClienteCommand(_editandoId.Value, _razonSocial, _cif, _esCritico, notas));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }

            if (mensajeError is not null)
            {
                _mensajeErrorFormulario = mensajeError;
                return;
            }

            if (_editandoId is not null && _puedeReasignarEjecutivo && _ejecutivoUsuarioId != _ejecutivoUsuarioIdOriginal)
            {
                var nuevoEjecutivoId = Guid.TryParse(_ejecutivoUsuarioId, out var idGestor) ? idGestor : (Guid?)null;
                var resultadoReasignar = await Mediator.Send(new ReasignarEjecutivoClienteCommand(_editandoId.Value, nuevoEjecutivoId));
                if (resultadoReasignar.EsFallido)
                    ToastService.Mostrar(resultadoReasignar.Error.Mensaje, TonoToast.Error);
            }

            ToastService.Mostrar(
                _editandoId is null ? "Cliente creado correctamente." : "Cliente actualizado correctamente.",
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
            var resultado = await Mediator.Send(new EliminarClienteCommand(_idAEliminar, usuarioId ?? Guid.Empty));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                ToastService.Mostrar("Cliente eliminado correctamente.", TonoToast.Exito);
                _confirmarEliminarVisible = false;
                await RecargarAsync();
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar el cliente. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }
}
