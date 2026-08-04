using System.Text.Json;
using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Clientes.Commands.EditarCliente;
using CaeManager.Application.Clientes.Commands.EliminarCliente;
using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Clientes.Commands.ReasignarEjecutivoCliente;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Infrastructure.Autorizacion;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.QuickGrid;

namespace CaeManager.Web.Features.Clientes.Pages;

public record GestorCaeSelectorDto(Guid Id, string NombreCompleto, string Email);

public partial class Clientes : ComponentBase
{
    [Inject] private DirectorioUsuariosTenant DirectorioUsuarios { get; set; } = default!;
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
    private Guid _versionEditando;
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

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    /// <summary>Comando del palette "Crear cliente" (P3-31): /clientes?accion=crear abre el Drawer directamente.</summary>
    [SupplyParameterFromQuery] public string? Accion { get; set; }

    private GridItemsProvider<ClienteListaDto>? _proveedorElementos;

    // --- P3-31: selección múltiple, atajos j/k, filtros guardados ---
    private readonly HashSet<Guid> _seleccionados = [];
    private List<ClienteListaDto> _elementosPagina = [];
    private Guid? _idEnfocado;
    private bool _eliminandoLote;
    private bool _confirmarEliminarLoteVisible;

    private IReadOnlyList<FiltroGuardadoDto> _filtrosGuardados = [];
    private bool _mostrarGuardarFiltro;
    private string _nombreFiltroNuevo = string.Empty;
    private bool _guardandoFiltro;

    private record FiltrosClientesJson(string? Busqueda, bool SoloCriticos);

    protected override async Task OnInitializedAsync()
    {
        // Delegado estable: pasar el grupo de método directamente en el
        // markup crea un delegado nuevo en cada render, QuickGrid lo trata
        // como "fuente de datos distinta" y recarga — combinado con el
        // StateHasChanged del proveedor, bucle infinito de recargas (visto
        // con PostgreSQL, donde el proveedor es asíncrono de verdad).
        _proveedorElementos = ProveerElementosAsync;

        // Reasignar el Gestor CAE dueño de un Cliente es una decisión de
        // rango superior (ver ReasignarEjecutivoClienteCommand) — el propio
        // Gestor CAE no ve ni puede tocar este campo sobre sus propios
        // clientes.
        var estadoAutenticacion = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        _puedeReasignarEjecutivo = RolesQuePuedenReasignar.Any(estadoAutenticacion.User.IsInRole);

        if (Accion == "crear")
            AbrirCrear();

        _filtrosGuardados = await Mediator.Send(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Clientes));
    }

    /// <summary>
    /// Se re-ejecuta en cada navegación dentro de la propia página (recargar,
    /// compartir la URL, volver atrás) — no solo en el primer render — para
    /// que el filtro de la URL sea la fuente de verdad, no solo su semilla
    /// inicial (P1-18 de docs/business/MATURITY_REVIEW.md). Permite además
    /// que el buscador global (Ctrl/Cmd+K) navegue aquí con el filtro ya
    /// cargado, p. ej. /clientes?q=Cadena+Industrial.
    /// </summary>
    protected override void OnParametersSet()
    {
        var deLaUrl = TerminoBusquedaInicial ?? string.Empty;
        if (deLaUrl != _busqueda)
            _busqueda = deLaUrl;
    }

    private async ValueTask<GridItemsProviderResult<ClienteListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<ClienteListaDto> request)
    {
        _cargando = true;
        _errorCarga = false;

        try
        {
            var pagina = (request.StartIndex / _paginacion.ItemsPerPage) + 1;
            var (ordenarPor, descendente) = LecturaOrden.Leer(request);

            var resultado = await Mediator.Send(new ObtenerClientesQuery(
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                SoloCriticos: _soloCriticos ? true : null,
                Pagina: pagina,
                TamanoPagina: _paginacion.ItemsPerPage,
                OrdenarPor: ordenarPor,
                Descendente: descendente));

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
        NavigationManager.ActualizarFiltroEnUrl("q", valor);
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
            // Acotado al tenant activo — ver DirectorioUsuariosTenant: sin
            // esto el selector ofrecía gestores de otras organizaciones.
            var gestores = await DirectorioUsuarios.ObtenerVisiblesEnRolAsync(Roles.GestorCae);
            _gestoresDisponibles = gestores
                .Select(u => new GestorCaeSelectorDto(u.Id, u.NombreCompleto, u.Email ?? string.Empty))
                .ToList();
        }

        _editandoId = cliente.Id;
        // La versión que se está viendo: vuelve en el Command para detectar
        // que otra persona guardó mientras el formulario estaba abierto.
        _versionEditando = cliente.Version;
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
                var resultado = await Mediator.Send(
                    new EditarClienteCommand(_editandoId.Value, _razonSocial, _cif, _esCritico, notas, _versionEditando));
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

    // --- P3-31: selección múltiple ---

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
            var resultado = await Mediator.Send(new EliminarClientesCommand(_seleccionados.ToList(), usuarioId ?? Guid.Empty));
            var dto = resultado.Valor;

            ToastService.Mostrar(
                dto.Errores.Count == 0
                    ? $"{dto.Eliminados} cliente(s) eliminado(s)."
                    : $"{dto.Eliminados} eliminado(s). {dto.Errores.Count} no se pudieron borrar: {string.Join(" ", dto.Errores)}",
                dto.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);

            _seleccionados.Clear();
            _confirmarEliminarLoteVisible = false;
            await RecargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar los clientes seleccionados. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _eliminandoLote = false;
        }
    }

    // --- P3-31: atajos de teclado j/k/x/Enter ---

    private string ObtenerClaseFila(ClienteListaDto item) => item.Id == _idEnfocado ? "fila-enfocada" : "";

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
                        await WorkspaceService.AbrirAsync(EntidadWorkspace.Cliente, elemento.Id, elemento.RazonSocial, "informacion");
                }
                break;
        }

        StateHasChanged();
    }

    // --- P3-31: filtros guardados ---

    private async Task AplicarFiltroGuardadoAsync(string idTexto)
    {
        if (!Guid.TryParse(idTexto, out var id)) return;

        var filtro = _filtrosGuardados.FirstOrDefault(f => f.Id == id);
        if (filtro is null) return;

        var valores = JsonSerializer.Deserialize<FiltrosClientesJson>(filtro.ValoresJson);
        if (valores is null) return;

        _busqueda = valores.Busqueda ?? string.Empty;
        _soloCriticos = valores.SoloCriticos;
        await RecargarAsync();
    }

    private async Task GuardarFiltroActualAsync()
    {
        if (string.IsNullOrWhiteSpace(_nombreFiltroNuevo)) return;

        _guardandoFiltro = true;

        try
        {
            var valoresJson = JsonSerializer.Serialize(new FiltrosClientesJson(
                string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda, _soloCriticos));

            var resultado = await Mediator.Send(
                new GuardarFiltroCommand(PantallasConFiltrosGuardados.Clientes, _nombreFiltroNuevo, valoresJson));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            _filtrosGuardados = await Mediator.Send(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Clientes));
            _mostrarGuardarFiltro = false;
            _nombreFiltroNuevo = string.Empty;
            ToastService.Mostrar("Filtro guardado.", TonoToast.Exito);
        }
        finally
        {
            _guardandoFiltro = false;
        }
    }

    private async Task EliminarFiltroGuardadoAsync(Guid id)
    {
        var resultado = await Mediator.Send(new EliminarFiltroGuardadoCommand(id));
        if (resultado.EsFallido)
        {
            ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            return;
        }

        _filtrosGuardados = await Mediator.Send(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Clientes));
    }
}
