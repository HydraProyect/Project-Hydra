using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Comunicaciones.Queries.ObtenerSugerenciaVisitaCorreo;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Visitas.Commands.CrearVisita;
using CaeManager.Application.Visitas.Commands.EditarVisita;
using CaeManager.Application.Visitas.Commands.EliminarVisita;
using CaeManager.Application.Visitas.Commands.MarcarNotificadoCliente;
using CaeManager.Application.Visitas.Queries.ObtenerVisitaPorId;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.QuickGrid;

namespace CaeManager.Web.Features.Visitas.Pages;

public partial class Visitas : ComponentBase
{
    private readonly PaginationState _paginacion = new() { ItemsPerPage = 20 };
    private QuickGrid<VisitaListaDto>? _grid;

    private string _busqueda = string.Empty;
    private bool _soloActivas = true;
    private string _filtroNotificado = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    private IReadOnlyList<CentroSelectorDto> _centrosDisponibles = [];
    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresDisponibles = [];
    private IReadOnlyList<ElementoSeleccionable> _trabajadoresDisponiblesSelector => _trabajadoresDisponibles
        .Select(t => new ElementoSeleccionable(t.Id, $"{t.NombreCompleto} ({t.Dni})"))
        .ToList();

    private bool _drawerVisible;
    private Guid? _editandoId;
    // Version del registro tal como se abrio: vuelve en el Command para
    // detectar que otra persona guardo mientras el formulario estaba abierto.
    private Guid _versionEditando;
    private string _centroId = string.Empty;
    private string _centroNombreEnEdicion = string.Empty;
    private string _fechaInicio = string.Empty;
    private string _fechaFin = string.Empty;
    private HashSet<Guid> _trabajadorIdsSeleccionados = [];
    private bool _notificadoCliente;
    private string _notas = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    // Prellenado desde "Crear visita" de una sugerencia detectada por IA en
    // un correo (ver SugerenciaVisitaCorreo) — se manda de vuelta en
    // CrearVisitaCommand para que el handler marque la sugerencia resuelta.
    private Guid? _sugerenciaVisitaCorreoId;
    private string? _sugerenciaVisitaResumen;

    [SupplyParameterFromQuery(Name = "sugerenciaId")]
    public string? SugerenciaVisitaIdInicial { get; set; }

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _centroAEliminar = string.Empty;
    private bool _eliminando;

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    [SupplyParameterFromQuery(Name = "notificado")]
    public string? NotificadoInicial { get; set; }

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private GridItemsProvider<VisitaListaDto>? _proveedorElementos;

    // Delegado estable — ver Clientes.razor.cs (bucle de recargas de QuickGrid).
    protected override void OnInitialized() => _proveedorElementos = ProveerElementosAsync;

    /// <summary>Abre el drawer prellenado si se llegó desde el botón "Crear visita" de una sugerencia de la Bandeja (?sugerenciaId=...).</summary>
    protected override async Task OnInitializedAsync()
    {
        if (Guid.TryParse(SugerenciaVisitaIdInicial, out var sugerenciaId))
            await AbrirCrearDesdeSugerenciaAsync(sugerenciaId);
    }

    /// <summary>
    /// Se re-ejecuta en cada navegación dentro de la propia página, no solo
    /// en el primer render — la URL como fuente de verdad de los filtros
    /// (P1-18 de docs/business/MATURITY_REVIEW.md).
    /// </summary>
    protected override void OnParametersSet()
    {
        _busqueda = TerminoBusquedaInicial ?? string.Empty;
        _filtroNotificado = NotificadoInicial ?? string.Empty;
    }

    private async ValueTask<GridItemsProviderResult<VisitaListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<VisitaListaDto> request)
    {
        _cargando = true;
        _errorCarga = false;

        try
        {
            var pagina = (request.StartIndex / _paginacion.ItemsPerPage) + 1;

            var resultado = await Mediator.Send(new ObtenerVisitasQuery(
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                SoloActivas: _soloActivas,
                NotificadoCliente: _filtroNotificado switch { "si" => true, "no" => false, _ => null },
                Pagina: pagina,
                TamanoPagina: _paginacion.ItemsPerPage));

            _totalElementos = resultado.TotalElementos;

            return GridItemsProviderResult.From(resultado.Elementos.ToList(), resultado.TotalElementos);
        }
        catch (Exception)
        {
            _errorCarga = true;
            return GridItemsProviderResult.From(new List<VisitaListaDto>(), 0);
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

    private async Task FiltrarPorNotificadoAsync(string valor)
    {
        _filtroNotificado = valor;
        NavigationManager.ActualizarFiltroEnUrl("notificado", valor);
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
        _centrosDisponibles = await Mediator.Send(new ObtenerCentrosParaSelectorQuery());
        _trabajadoresDisponibles = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery());

        _editandoId = null;
        _centroId = string.Empty;
        _centroNombreEnEdicion = string.Empty;
        _fechaInicio = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        _fechaFin = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        _trabajadorIdsSeleccionados = [];
        _notificadoCliente = false;
        _notas = string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _sugerenciaVisitaCorreoId = null;
        _sugerenciaVisitaResumen = null;
        _drawerVisible = true;
    }

    /// <summary>Variante de AbrirCrearAsync que prellena Centro/fechas/notas con lo que detectó la IA en un correo — el Gestor sigue teniendo que elegir los trabajadores y confirmar el resto a mano.</summary>
    private async Task AbrirCrearDesdeSugerenciaAsync(Guid sugerenciaId)
    {
        var sugerencia = await Mediator.Send(new ObtenerSugerenciaVisitaCorreoQuery(sugerenciaId));
        if (sugerencia is null)
        {
            ToastService.Mostrar("No encontramos esta sugerencia. Puede que ya se haya resuelto.", TonoToast.Error);
            return;
        }

        await AbrirCrearAsync();

        _sugerenciaVisitaCorreoId = sugerencia.Id;
        _sugerenciaVisitaResumen = sugerencia.Resumen;
        _centroId = sugerencia.CentroId?.ToString() ?? string.Empty;
        if (sugerencia.FechaInicio is not null) _fechaInicio = sugerencia.FechaInicio.Value.ToString("yyyy-MM-dd");
        if (sugerencia.FechaFin is not null) _fechaFin = sugerencia.FechaFin.Value.ToString("yyyy-MM-dd");
    }

    private async Task AbrirEditarAsync(Guid id)
    {
        _sugerenciaVisitaCorreoId = null;
        _sugerenciaVisitaResumen = null;
        _trabajadoresDisponibles = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery());

        var visita = await Mediator.Send(new ObtenerVisitaPorIdQuery(id));
        if (visita is null)
        {
            ToastService.Mostrar("No encontramos esta visita. Puede que ya se haya eliminado.", TonoToast.Error);
            await RecargarAsync();
            return;
        }

        _editandoId = visita.Id;
        _versionEditando = visita.Version;
        _centroId = visita.CentroId.ToString();
        _centroNombreEnEdicion = $"{visita.CentroNombre} ({visita.ClienteRazonSocial} — {visita.EmpresaRazonSocial})";
        _fechaInicio = visita.FechaInicio.ToString("yyyy-MM-dd");
        _fechaFin = visita.FechaFin.ToString("yyyy-MM-dd");
        _trabajadorIdsSeleccionados = visita.TrabajadorIds.ToHashSet();
        _notificadoCliente = visita.NotificadoCliente;
        _notas = visita.Notas ?? string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private void AlternarTrabajador(Guid trabajadorId, bool seleccionado)
    {
        if (seleccionado)
            _trabajadorIdsSeleccionados.Add(trabajadorId);
        else
            _trabajadorIdsSeleccionados.Remove(trabajadorId);
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
            if (!DateOnly.TryParse(_fechaInicio, out var fechaInicio) || !DateOnly.TryParse(_fechaFin, out var fechaFin))
            {
                _mensajeErrorFormulario = "Las fechas no son válidas.";
                return;
            }

            var notas = string.IsNullOrWhiteSpace(_notas) ? null : _notas;
            var trabajadorIds = _trabajadorIdsSeleccionados.ToList();
            string? mensajeError;

            if (_editandoId is null)
            {
                if (!Guid.TryParse(_centroId, out var centroId))
                {
                    _mensajeErrorFormulario = "Selecciona un centro.";
                    return;
                }

                var resultado = await Mediator.Send(new CrearVisitaCommand(centroId, fechaInicio, fechaFin, trabajadorIds, notas, _sugerenciaVisitaCorreoId));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }
            else
            {
                var resultado = await Mediator.Send(new EditarVisitaCommand(_editandoId.Value, fechaInicio, fechaFin, trabajadorIds, notas, _versionEditando));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }

            if (mensajeError is not null)
            {
                _mensajeErrorFormulario = mensajeError;
                return;
            }

            ToastService.Mostrar(
                _editandoId is null ? "Visita creada correctamente." : "Visita actualizada correctamente.",
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

    private async Task AlternarNotificadoAsync(Guid id, bool notificado)
    {
        try
        {
            var resultado = await Mediator.Send(new MarcarNotificadoClienteCommand(id, notificado));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                await RecargarAsync();
                return;
            }

            await RecargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos actualizar el estado de notificación. Intenta nuevamente.", TonoToast.Error);
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
            var usuarioId = await CurrentUserService.ObtenerUsuarioActualIdAsync();
            var resultado = await Mediator.Send(new EliminarVisitaCommand(_idAEliminar, usuarioId ?? Guid.Empty));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                ToastService.Mostrar("Visita eliminada correctamente.", TonoToast.Exito);
                _confirmarEliminarVisible = false;
                await RecargarAsync();
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar la visita. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }
}
