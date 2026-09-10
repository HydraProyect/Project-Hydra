using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Proyectos.Commands.ActualizarProyecto;
using CaeManager.Application.Proyectos.Commands.AsignarTecnicoProyecto;
using CaeManager.Application.Proyectos.Commands.CerrarProyecto;
using CaeManager.Application.Proyectos.Commands.CrearProyecto;
using CaeManager.Application.Proyectos.Commands.DesasignarTecnicoProyecto;
using CaeManager.Application.Proyectos.Commands.EliminarProyecto;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectoPorId;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Application.Proyectos.Queries.ObtenerTecnicosProyecto;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Proyectos.Pages;

public partial class Proyectos : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ILogger<Proyectos> Logger { get; set; } = default!;

    private bool _cargando = true;
    private bool _errorCarga;
    private bool _cargandoProyectos;
    private bool _errorProyectos;

    private IReadOnlyList<ClienteSelectorDto> _clientes = [];
    private Guid _clienteSeleccionadoId = Guid.Empty;
    private IReadOnlyList<CentroSelectorDto> _centrosDisponibles = [];

    /// <summary>
    /// La lista COMPLETA de proyectos del cliente elegido: ObtenerProyectosQuery
    /// no pagina. Los filtros de estado y búsqueda se aplican en memoria sobre
    /// ella (<see cref="ProyectosVisibles"/>), y por eso la pantalla distingue
    /// sin mentir "el cliente no tiene proyectos" de "ninguno coincide".
    /// </summary>
    private List<ProyectoListaDto> _proyectos = [];

    private string _pestanaDetalle = "informacion";

    private static DateOnly Hoy => DateOnly.FromDateTime(DateTime.UtcNow);

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            _clientes = await Mediator.Send(new ObtenerClientesParaSelectorQuery());
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    private Task OnClienteSeleccionadoAsync(string valor)
    {
        _clienteSeleccionadoId = Guid.TryParse(valor, out var id) ? id : Guid.Empty;
        return OnClienteChangedAsync();
    }

    private async Task OnClienteChangedAsync()
    {
        // Invalida la carga del cliente anterior también cuando se vuelve a
        // "ningún cliente", que no arranca carga propia que la sustituya.
        _versionCargaCliente++;
        _cargandoProyectos = false;
        _proyectos = [];
        _centrosDisponibles = [];
        _errorProyectos = false;
        CerrarDetalle();

        if (_clienteSeleccionadoId == Guid.Empty)
            return;

        await CargarDatosClienteAsync();
    }

    /// <summary>
    /// Número de la carga de datos de cliente vigente. Cada carga (cambio de
    /// cliente, "Reintentar", recarga tras crear o cerrar) toma uno nuevo y,
    /// tras cada <c>await</c>, solo escribe si sigue siendo la vigente: sin
    /// esto, cambiar de cliente A→B con la carga de A en curso dejaba que la
    /// respuesta tardía de A pintase sus centros, proyectos o error bajo B.
    /// </summary>
    private int _versionCargaCliente;

    /// <summary>
    /// Centros (para el alta) y proyectos del cliente elegido. Es también lo
    /// que repite "Reintentar": antes un fallo aquí no tenía estado propio y
    /// subía sin capturar.
    /// </summary>
    private async Task CargarDatosClienteAsync()
    {
        var version = ++_versionCargaCliente;
        var clienteId = _clienteSeleccionadoId;
        _cargandoProyectos = true;
        _errorProyectos = false;
        StateHasChanged();

        try
        {
            var centros = await Mediator.Send(new ObtenerCentrosParaSelectorQuery(ClienteId: clienteId));
            if (version != _versionCargaCliente) return;
            _centrosDisponibles = centros;

            var proyectos = await Mediator.Send(new ObtenerProyectosQuery(clienteId));
            if (version != _versionCargaCliente) return;
            _proyectos = proyectos.ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "No se pudieron cargar los proyectos del cliente {ClienteId}.", clienteId);
            if (version != _versionCargaCliente) return;
            _proyectos = [];
            _errorProyectos = true;
        }
        finally
        {
            if (version == _versionCargaCliente)
                _cargandoProyectos = false;
        }
    }

    private async Task CargarProyectosAsync()
    {
        var version = ++_versionCargaCliente;
        var clienteId = _clienteSeleccionadoId;
        _cargandoProyectos = true;
        _errorProyectos = false;
        StateHasChanged();

        try
        {
            var proyectos = await Mediator.Send(new ObtenerProyectosQuery(clienteId));
            if (version != _versionCargaCliente) return;
            _proyectos = proyectos.ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "No se pudieron recargar los proyectos del cliente {ClienteId}.", clienteId);
            if (version != _versionCargaCliente) return;
            _proyectos = [];
            _errorProyectos = true;
        }
        finally
        {
            if (version == _versionCargaCliente)
                _cargandoProyectos = false;
        }
    }

    // ---- Filtros (estado y búsqueda, en la URL) ----

    private const string EstadoAbiertos = "abiertos";
    private const string EstadoCerrados = "cerrados";

    private static readonly IReadOnlyList<OpcionEstado> OpcionesEstado =
        [new(EstadoAbiertos, "Abiertos"), new(EstadoCerrados, "Cerrados")];

    private string _busqueda = string.Empty;
    private string _estadoFiltro = string.Empty;

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    [SupplyParameterFromQuery(Name = "estado")]
    public string? EstadoInicial { get; set; }

    /// <summary>
    /// La URL es la fuente de verdad de los dos filtros, no solo su semilla:
    /// se re-sincroniza en cada navegación dentro de la página (mismo criterio
    /// que Vehiculos.razor.cs).
    /// </summary>
    protected override void OnParametersSet()
    {
        var busquedaDeLaUrl = TerminoBusquedaInicial ?? string.Empty;
        if (busquedaDeLaUrl != _busqueda)
            _busqueda = busquedaDeLaUrl;

        var estadoDeLaUrl = OpcionesEstado.Any(o => o.Valor == EstadoInicial) ? EstadoInicial! : string.Empty;
        if (estadoDeLaUrl != _estadoFiltro)
            _estadoFiltro = estadoDeLaUrl;
    }

    private bool HayFiltrosActivos =>
        !string.IsNullOrWhiteSpace(_busqueda) || !string.IsNullOrWhiteSpace(_estadoFiltro);

    private IReadOnlyList<ProyectoListaDto> ProyectosVisibles => _proyectos.Where(CumpleFiltros).ToList();

    private bool CumpleFiltros(ProyectoListaDto proyecto)
    {
        var cumpleEstado = _estadoFiltro switch
        {
            EstadoAbiertos => proyecto.EstaAbierto,
            EstadoCerrados => !proyecto.EstaAbierto,
            _ => true
        };

        if (!cumpleEstado) return false;

        var termino = _busqueda.Trim();
        return termino.Length == 0
            || proyecto.Nombre.Contains(termino, StringComparison.OrdinalIgnoreCase)
            || proyecto.CentroNombre.Contains(termino, StringComparison.OrdinalIgnoreCase);
    }

    private string TextoEstadoFiltro =>
        OpcionesEstado.FirstOrDefault(o => o.Valor == _estadoFiltro)?.Texto.ToLowerInvariant() ?? string.Empty;

    private string TextoConteo => HayFiltrosActivos
        ? $"{ProyectosVisibles.Count} de {_proyectos.Count} proyecto(s) de este cliente"
        : $"{_proyectos.Count} proyecto(s) de este cliente";

    private Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        NavigationManager.ActualizarFiltroEnUrl("q", valor);
        return Task.CompletedTask;
    }

    private Task CambiarEstadoAsync(string valor)
    {
        _estadoFiltro = valor;
        NavigationManager.ActualizarFiltroEnUrl("estado", valor);
        return Task.CompletedTask;
    }

    private Task QuitarBusquedaAsync() => BuscarAsync(string.Empty);

    private Task QuitarEstadoAsync() => CambiarEstadoAsync(string.Empty);

    /// <summary>
    /// Quita los dos filtros, y los dos TAMBIÉN de la URL en una sola
    /// navegación: <see cref="OnParametersSet"/> re-sincroniza desde la URL,
    /// así que dejarlos allí los devolvería en cuanto el router volviera a
    /// pasar. El cliente elegido no es un filtro: es el maestro de la lista y
    /// se queda como está.
    /// </summary>
    private Task LimpiarFiltrosAsync()
    {
        _busqueda = string.Empty;
        _estadoFiltro = string.Empty;
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?> { ["q"] = null, ["estado"] = null });
        return Task.CompletedTask;
    }

    // ---- Nuevo proyecto (Drawer) ----

    private bool _drawerVisible;
    private string _nuevoCentroId = string.Empty;
    private string _nuevoNombre = string.Empty;
    private string _nuevaFechaInicio = string.Empty;
    private string _nuevaFechaFinPrevista = string.Empty;
    private string _nuevasNotas = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private void AbrirNuevoProyecto()
    {
        _nuevoCentroId = string.Empty;
        _nuevoNombre = string.Empty;
        _nuevaFechaInicio = Hoy.ToString("yyyy-MM-dd");
        _nuevaFechaFinPrevista = string.Empty;
        _nuevasNotas = string.Empty;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();
        _drawerVisible = true;
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        return Task.CompletedTask;
    }

    private async Task CrearProyectoAsync()
    {
        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();
        StateHasChanged();

        try
        {
            if (!Guid.TryParse(_nuevoCentroId, out var centroId))
            {
                _mensajeErrorFormulario = "Selecciona un centro.";
                return;
            }

            if (!DateOnly.TryParse(_nuevaFechaInicio, out var fechaInicio))
            {
                _mensajeErrorFormulario = "Introduce una fecha de inicio válida.";
                return;
            }

            DateOnly? fechaFinPrevista = DateOnly.TryParse(_nuevaFechaFinPrevista, out var fv) ? fv : null;
            var notas = string.IsNullOrWhiteSpace(_nuevasNotas) ? null : _nuevasNotas;

            var resultado = await Mediator.Send(new CrearProyectoCommand(
                _clienteSeleccionadoId, centroId, _nuevoNombre, fechaInicio, fechaFinPrevista, notas));

            if (resultado.EsFallido)
            {
                _mensajeErrorFormulario = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Proyecto creado correctamente.", TonoToast.Exito);
            _drawerVisible = false;
            await CargarProyectosAsync();
        }
        catch (ValidationException ex)
        {
            _erroresCampo = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _mensajeErrorFormulario = "No pudimos crear el proyecto. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardando = false;
        }
    }

    // ---- Detalle de proyecto (panel lateral: Información, Técnicos, Documentos) ----

    private Guid? _proyectoSeleccionadoId;
    private ProyectoDetalleDto? _detalle;
    private bool _cargandoDetalle;

    /// <summary>
    /// Número de la selección de detalle vigente: cada selección y cada cierre
    /// del panel toman uno nuevo. Pulsar A y enseguida B dejaba que la
    /// respuesta tardía de A se pintase en el panel de B —y Editar/Cerrar,
    /// que usan <c>_detalle.Id</c>, operaban sobre A—. También invalida los
    /// técnicos pedidos para un detalle que ya no está abierto.
    /// </summary>
    private int _versionDetalle;

    private async Task SeleccionarProyectoAsync(Guid id)
    {
        var version = ++_versionDetalle;
        _proyectoSeleccionadoId = id;
        _pestanaDetalle = "informacion";
        _editandoInfo = false;
        _mostrarFormularioTecnico = false;
        _cargandoDetalle = true;
        _detalle = null;
        _tecnicos = [];
        _cargandoTecnicos = false;
        StateHasChanged();

        try
        {
            var detalle = await Mediator.Send(new ObtenerProyectoPorIdQuery(id));
            if (version != _versionDetalle) return;

            _detalle = detalle;
            if (_detalle is not null)
            {
                _editNombre = _detalle.Nombre;
                _editFechaFinPrevista = _detalle.FechaFinPrevista?.ToString("yyyy-MM-dd") ?? string.Empty;
                _editNotas = _detalle.Notas ?? string.Empty;
            }
        }
        finally
        {
            if (version == _versionDetalle)
                _cargandoDetalle = false;
        }
    }

    private void CerrarDetalle()
    {
        _versionDetalle++;
        _proyectoSeleccionadoId = null;
        _detalle = null;
        _editandoInfo = false;
        _mostrarCerrarConfirm = false;
    }

    private Task CambiarPestanaDetalleAsync(string pestana)
    {
        _pestanaDetalle = pestana;

        if (pestana == "tecnicos" && _detalle is not null && _tecnicos.Count == 0 && !_cargandoTecnicos)
            return CargarTecnicosAsync();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Días del periodo abierto del proyecto: de <paramref name="inicio"/> al
    /// cierre real o, si sigue abierto, a <paramref name="hoy"/>. Cuenta
    /// INCLUSIVA —el día de inicio y el de cierre cuentan los dos—, la misma
    /// que usa la facturación por días de proyecto abierto
    /// (ObtenerResumenFacturacionQuery, <c>hasta - desde + 1</c>): dos cifras
    /// de "días abiertos" que no cuadrasen entre sí serían peor que ninguna.
    /// Sin valor si el proyecto todavía no ha empezado.
    /// </summary>
    private static int? DiasAbiertos(DateOnly inicio, DateOnly? cierre, DateOnly hoy)
    {
        var fin = cierre ?? hoy;
        return fin < inicio ? null : fin.DayNumber - inicio.DayNumber + 1;
    }

    private static string TextoTecnicosActivos(int tecnicosActivos) =>
        tecnicosActivos == 1 ? "1 técnico activo" : $"{tecnicosActivos} técnicos activos";

    private static string MetaTecnico(TecnicoProyectoDto tecnico)
    {
        var partes = new List<string>();
        if (!string.IsNullOrWhiteSpace(tecnico.TrabajadorDni))
            partes.Add(tecnico.TrabajadorDni);
        partes.Add($"alta {tecnico.FechaAlta:dd/MM/yyyy}");
        if (tecnico.FechaBaja is { } baja)
            partes.Add($"baja {baja:dd/MM/yyyy}");
        return string.Join(" · ", partes);
    }

    // ---- Editar información ----

    private bool _editandoInfo;
    private string _editNombre = string.Empty;
    private string _editFechaFinPrevista = string.Empty;
    private string _editNotas = string.Empty;
    private Dictionary<string, string> _editErrores = new();
    private string? _editError;

    /// <summary>"Editar" vive en el pie del panel: lleva a la pestaña Información, que es la que se edita.</summary>
    private void IniciarEdicionInfo()
    {
        _pestanaDetalle = "informacion";
        _editandoInfo = true;
    }

    private void CancelarEdicionInfo()
    {
        _editandoInfo = false;
        _editErrores = new();
        _editError = null;

        if (_detalle is not null)
        {
            _editNombre = _detalle.Nombre;
            _editFechaFinPrevista = _detalle.FechaFinPrevista?.ToString("yyyy-MM-dd") ?? string.Empty;
            _editNotas = _detalle.Notas ?? string.Empty;
        }
    }

    private async Task GuardarEdicionInfoAsync()
    {
        if (_detalle is null) return;

        // El id se fija antes del await: mientras se guarda, el usuario puede
        // abrir otro proyecto y _detalle pasar a ser otro, o null mientras
        // carga (y entonces _detalle.Id reventaba tras un guardado correcto).
        var id = _detalle.Id;
        _editErrores = new();
        _editError = null;
        _guardando = true;
        StateHasChanged();

        try
        {
            DateOnly? fechaFinPrevista = DateOnly.TryParse(_editFechaFinPrevista, out var fv) ? fv : null;
            var notas = string.IsNullOrWhiteSpace(_editNotas) ? null : _editNotas;

            var resultado = await Mediator.Send(
                new ActualizarProyectoCommand(id, _editNombre, fechaFinPrevista, notas, _detalle.Version));

            if (resultado.EsFallido)
            {
                _editError = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Proyecto actualizado correctamente.", TonoToast.Exito);
            _editandoInfo = false;

            // Solo se refresca el detalle si sigue siendo el abierto: si el
            // usuario ya eligió otro, recargar este le devolvería el panel.
            if (_proyectoSeleccionadoId == id)
                await SeleccionarProyectoAsync(id);

            await CargarProyectosAsync();
        }
        catch (ValidationException ex)
        {
            _editErrores = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _editError = "No pudimos guardar los cambios. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardando = false;
        }
    }

    // ---- Cerrar proyecto ----

    private bool _mostrarCerrarConfirm;

    /// <summary>
    /// El proyecto que el modal va a cerrar. Separado de
    /// <see cref="_proyectoSeleccionadoId"/> a propósito: antes el modal
    /// reutilizaba la selección del detalle, y cerrar desde la fila de un
    /// proyecto sin detalle abierto hacía aparecer el detalle vacío con
    /// "No pudimos cargar este proyecto".
    /// </summary>
    private Guid? _idACerrar;
    private string _fechaCierre = string.Empty;
    private string? _errorCierre;

    private void AbrirCerrarConfirm(Guid id)
    {
        _idACerrar = id;
        _fechaCierre = Hoy.ToString("yyyy-MM-dd");
        _errorCierre = null;
        _mostrarCerrarConfirm = true;
    }

    private async Task ConfirmarCerrarAsync()
    {
        if (_idACerrar is not { } idACerrar) return;

        if (!DateOnly.TryParse(_fechaCierre, out var fechaCierre))
        {
            _errorCierre = "Introduce una fecha de cierre válida.";
            return;
        }

        _guardando = true;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new CerrarProyectoCommand(idACerrar, fechaCierre));

            if (resultado.EsFallido)
            {
                _errorCierre = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Proyecto cerrado correctamente.", TonoToast.Exito);
            _mostrarCerrarConfirm = false;
            await CargarProyectosAsync();

            if (_detalle is not null && _detalle.Id == idACerrar)
                await SeleccionarProyectoAsync(_detalle.Id);
        }
        finally
        {
            _guardando = false;
        }
    }

    // ---- Eliminar proyecto ----

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _nombreAEliminar = string.Empty;
    private bool _eliminando;

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
            var resultado = await Mediator.Send(new EliminarProyectoCommand(_idAEliminar));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Proyecto eliminado.", TonoToast.Exito);
            _confirmarEliminarVisible = false;

            if (_proyectoSeleccionadoId == _idAEliminar)
                CerrarDetalle();

            await CargarProyectosAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar el proyecto. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }

    // ---- Técnicos ----

    private List<TecnicoProyectoDto> _tecnicos = [];
    private bool _cargandoTecnicos;
    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresDisponibles = [];
    private bool _mostrarFormularioTecnico;
    private string _nuevoTecnicoTrabajadorId = string.Empty;
    private string _nuevoTecnicoFechaAlta = string.Empty;
    private string? _errorTecnico;

    private async Task CargarTecnicosAsync()
    {
        if (_detalle is null) return;

        var version = _versionDetalle;
        _cargandoTecnicos = true;
        StateHasChanged();

        try
        {
            var tecnicos = await Mediator.Send(new ObtenerTecnicosProyectoQuery(_detalle.Id));
            if (version != _versionDetalle) return;
            _tecnicos = tecnicos.ToList();
        }
        finally
        {
            if (version == _versionDetalle)
                _cargandoTecnicos = false;
        }
    }

    private async Task AbrirFormularioTecnicoAsync()
    {
        if (_trabajadoresDisponibles.Count == 0)
            _trabajadoresDisponibles = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery());

        _nuevoTecnicoTrabajadorId = string.Empty;
        _nuevoTecnicoFechaAlta = Hoy.ToString("yyyy-MM-dd");
        _errorTecnico = null;
        _mostrarFormularioTecnico = true;
    }

    private async Task AsignarTecnicoAsync()
    {
        if (_detalle is null) return;

        if (!Guid.TryParse(_nuevoTecnicoTrabajadorId, out var trabajadorId))
        {
            _errorTecnico = "Selecciona un técnico.";
            return;
        }

        if (!DateOnly.TryParse(_nuevoTecnicoFechaAlta, out var fechaAlta))
        {
            _errorTecnico = "Introduce una fecha de alta válida.";
            return;
        }

        _guardando = true;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new AsignarTecnicoProyectoCommand(_detalle.Id, trabajadorId, fechaAlta));

            if (resultado.EsFallido)
            {
                _errorTecnico = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Técnico asignado correctamente.", TonoToast.Exito);
            _mostrarFormularioTecnico = false;
            await CargarTecnicosAsync();
        }
        finally
        {
            _guardando = false;
        }
    }

    private async Task DesasignarTecnicoAsync(Guid id)
    {
        try
        {
            var resultado = await Mediator.Send(new DesasignarTecnicoProyectoCommand(id, Hoy));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Técnico dado de baja del proyecto.", TonoToast.Exito);
            await CargarTecnicosAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos dar de baja al técnico. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
    }
}
