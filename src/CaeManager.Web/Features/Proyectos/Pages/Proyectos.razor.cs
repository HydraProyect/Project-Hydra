using CaeManager.Domain.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Components.Layout;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Proyectos.Commands.ActualizarProyecto;
using CaeManager.Application.Proyectos.Commands.AsignarTecnicoProyecto;
using CaeManager.Application.Proyectos.Commands.CerrarProyecto;
using CaeManager.Application.Proyectos.Commands.ReabrirProyecto;
using CaeManager.Application.Proyectos.Commands.CrearProyecto;
using CaeManager.Application.Proyectos.Commands.DesasignarTecnicoProyecto;
using CaeManager.Application.Proyectos.Commands.EliminarProyecto;
using CaeManager.Application.Proyectos.Commands.RestaurarProyecto;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectoPorId;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Application.Proyectos.Queries.ObtenerTecnicosProyecto;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace CaeManager.Web.Features.Proyectos.Pages;

public partial class Proyectos : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    /// <summary>
    /// Se cancela al salir de la página: la resolución de la empresa activa que siga en vuelo deja de trabajar
    /// para nadie y su respuesta tardía no repinta un componente ya retirado.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    public void Dispose()
    {
        if (_desechado)
            return;

        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    /// <summary>La empresa gestionada activa, solo para quien ve el selector de la barra lateral.</summary>
    private ClienteAutorizadoDto? _empresaActiva;

    /// <summary>Estado 4a del mockup del selector: hay que elegir una empresa de la cartera antes de ver la lista.</summary>
    private bool _sinEmpresaSeleccionada;

    /// <summary>La empresa activa aún no se ha resuelto: se pinta una carga en vez de la lista.</summary>
    private bool _resolviendoEmpresa = true;

    [Inject] private ITenantActual TenantActual { get; set; } = default!;
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

    private static DateOnly Hoy => DiaDeNegocio.Hoy();

    protected override async Task OnInitializedAsync()
    {
        // Hasta resolver la empresa activa no se monta la lista ni sus acciones: con la consulta en
        // vuelo el render saldría con «hay empresa» y lanzaría la carga del Tenant de origen.
        try
        {
            var contexto = await ContextoEmpresaActiva.ResolverAsync(Mediator, TenantActual, _ciclo.Token);
            _empresaActiva = contexto.Activa;
            _sinEmpresaSeleccionada = contexto.SinSeleccion;
        }
        catch (OperationCanceledException) when (_ciclo.IsCancellationRequested)
        {
            // La página se retiró con la resolución en vuelo: no queda nadie a quien pintar.
            return;
        }
        finally
        {
            _resolviendoEmpresa = false;
        }

        // Retirada la página, una resolución que vuelva sin lanzar (contexto «Ninguno») no es 4a: no hay
        // a quién pintarle la lista, y seguir pediría los datos de una página que ya no existe.
        if (_desechado)
            return;

        // Sin empresa elegida no se piden los datos de la organización de origen.
        if (_sinEmpresaSeleccionada)
            return;

        await CargarAsync();
    }

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

        // Un enlace o una recarga con ?cliente= abre la lista de ese Cliente empresarial.
        await AplicarClienteDeLaUrlAsync();
    }

    /// <summary>
    /// Cliente empresarial elegido, en la URL (<c>?cliente=</c>) como en Centros:
    /// es el maestro de la lista, y sin él recargar o compartir el enlace
    /// volvía a «Elige un Cliente».
    /// </summary>
    [SupplyParameterFromQuery(Name = "cliente")]
    public string? ClienteInicial { get; set; }

    /// <summary>
    /// Elige el Cliente empresarial que pide la URL si es uno de los que este
    /// usuario puede elegir y no es ya el elegido. Un Id ajeno a la lista, o la
    /// ausencia del parámetro, no cambian nada: la URL no puede abrir un Cliente
    /// empresarial que el selector no ofrece. Pasa por
    /// <see cref="OnClienteSeleccionadoAsync"/>, así que pregunta antes de
    /// perder lo escrito en el panel de detalle.
    ///
    /// <para>
    /// <b>No escribe en la URL</b>: ya dice ese Cliente empresarial, y esta ruta
    /// corre también en <c>OnInitializedAsync</c> durante el prerender, donde un
    /// <c>NavigateTo</c> es una redirección HTTP — a la misma dirección, en bucle.
    /// </para>
    /// </summary>
    private async Task AplicarClienteDeLaUrlAsync()
    {
        if (_cargando || _errorCarga)
            return;
        if (Guid.TryParse(ClienteInicial, out var id) && id != _clienteSeleccionadoId && _clientes.Any(c => c.Id == id))
            await SeleccionarClienteAsync(id, pedidoPorLaUrl: true);
    }

    private void EscribirClienteEnUrl() =>
        NavigationManager.ActualizarFiltroEnUrl(
            "cliente", _clienteSeleccionadoId == Guid.Empty ? null : _clienteSeleccionadoId.ToString());

    private Task OnClienteSeleccionadoAsync(string valor) =>
        SeleccionarClienteAsync(Guid.TryParse(valor, out var id) ? id : Guid.Empty, pedidoPorLaUrl: false);

    private async Task SeleccionarClienteAsync(Guid nuevo, bool pedidoPorLaUrl)
    {
        // Cambiar de Cliente empresarial cierra el panel de detalle (OnClienteChangedAsync): si
        // tenía algo escrito, se pregunta antes y, si se sigue editando, la selección vuelve
        // al Cliente empresarial anterior y se renueva el selector.
        if (nuevo != _clienteSeleccionadoId && !await _ambitoDetalle.ConfirmarAbandonoAsync())
        {
            _versionSelectorCliente++;
            _focoSelectorClienteEmpresarialPendiente = true;
            // Si el cambio lo pedía la URL, vuelve a decir el Cliente empresarial que sigue en pantalla.
            // Pedido desde el selector no se navega: la URL no ha cambiado, y una navegación con el
            // panel todavía sin guardar volvería a preguntar «¿Salir sin guardar?».
            if (pedidoPorLaUrl)
                EscribirClienteEnUrl();
            return;
        }

        _clienteSeleccionadoId = nuevo;
        // Primero se cierra el panel de detalle; la URL se escribe ya sin nada pendiente de guardar.
        await OnClienteChangedAsync();
        if (!pedidoPorLaUrl)
            EscribirClienteEnUrl();
    }

    private int _versionSelectorCliente;
    private PastillaFiltro? _selectorClienteEmpresarial;
    private bool _focoSelectorClienteEmpresarialPendiente;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);
        if (_focoSelectorClienteEmpresarialPendiente && !_desechado
            && !_resolviendoEmpresa && !_sinEmpresaSeleccionada && !_cargando && !_errorCarga
            && _selectorClienteEmpresarial is { } selector)
        {
            _focoSelectorClienteEmpresarialPendiente = false;
            await selector.EnfocarAsync();
        }
    }

    private IReadOnlyList<OpcionEstado> OpcionesClienteListado =>
    [
        new(string.Empty, Textos["ListaElegirClienteEmpresarial"].Value),
        .. _clientes.Select(c => new OpcionEstado(c.Id.ToString(), c.RazonSocial))
    ];

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

    // De instancia, no static: las etiquetas salen del localizador inyectado.
    private IReadOnlyList<OpcionFranjaEstado> OpcionesEstado =>
        [new(Textos["FiltroAbiertos"], TonoBadge.Exito, EstadoAbiertos), new(Textos["FiltroCerrados"], TonoBadge.Neutro, EstadoCerrados)];

    /// <summary>
    /// La selección de estados que llega de la URL reducida a los dos que existen; lo demás se descarta.
    /// Cadena vacía si no queda ninguno.
    /// </summary>
    private static string EstadosValidos(string? seleccion) =>
        SeleccionEstados.Unir(SeleccionEstados.Separar(seleccion).Where(v => v is EstadoAbiertos or EstadoCerrados)) ?? string.Empty;

    /// <summary>Proyectos por estado para la franja: con la búsqueda aplicada y sin el filtro de estado.</summary>
    private IReadOnlyDictionary<string, int> RecuentosPorEstado
    {
        get
        {
            var conBusqueda = _proyectos.Where(CumpleBusqueda).ToList();
            var abiertos = conBusqueda.Count(p => p.EstaAbierto);
            return new Dictionary<string, int> { [EstadoAbiertos] = abiertos, [EstadoCerrados] = conBusqueda.Count - abiertos };
        }
    }

    private string _busqueda = string.Empty;
    private string _estadoFiltro = string.Empty;

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    [SupplyParameterFromQuery(Name = "estado")]
    public string? EstadoInicial { get; set; }

    /// <summary>
    /// La URL es la fuente de verdad de los dos filtros, no solo su semilla:
    /// se re-sincroniza en cada navegación dentro de la página (mismo criterio
    /// que Vehiculos.razor.cs). Mientras se resuelve la empresa activa, en el
    /// estado 4a y con la página retirada la URL no se sincroniza.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        // Retirada la página con la resolución en vuelo, ComponentBase aún invoca esto: no se procesan
        // parámetros de la URL de un componente que ya no existe, ni en el estado 4a.
        if (_desechado || _resolviendoEmpresa || _sinEmpresaSeleccionada)
            return;

        var busquedaDeLaUrl = TerminoBusquedaInicial ?? string.Empty;
        if (busquedaDeLaUrl != _busqueda)
            _busqueda = busquedaDeLaUrl;

        var estadoDeLaUrl = EstadosValidos(EstadoInicial);
        if (estadoDeLaUrl != _estadoFiltro)
            _estadoFiltro = estadoDeLaUrl;

        // Los dos filtros cambian navegando, así que este es el único punto por
        // el que pasan todos: si la fila enfocada deja de estar visible, el
        // foco se descarta aquí. Conservarlo escondido lo haría reaparecer al
        // quitar el filtro, sobre una fila que el usuario ya no tenía delante.
        if (_idEnfocado is { } idEnfocado && !_proyectos.Any(p => p.Id == idEnfocado && CumpleFiltros(p)))
            _idEnfocado = null;

        await AplicarClienteDeLaUrlAsync();
    }

    private bool HayFiltrosActivos =>
        !string.IsNullOrWhiteSpace(_busqueda) || !string.IsNullOrWhiteSpace(_estadoFiltro);

    private IReadOnlyList<ProyectoListaDto> ProyectosVisibles => _proyectos.Where(CumpleFiltros).ToList();

    private bool CumpleFiltros(ProyectoListaDto proyecto)
    {
        // Varios estados marcados: pasa el Proyecto que esté en cualquiera. Sin ninguno, todos.
        var marcados = SeleccionEstados.Separar(_estadoFiltro);
        var cumpleEstado = marcados.Count == 0
            || marcados.Contains(proyecto.EstaAbierto ? EstadoAbiertos : EstadoCerrados);

        return cumpleEstado && CumpleBusqueda(proyecto);
    }

    private bool CumpleBusqueda(ProyectoListaDto proyecto)
    {
        var termino = _busqueda.Trim();
        return termino.Length == 0
            || TextoDeBusqueda.Contiene(proyecto.Nombre, termino)
            || TextoDeBusqueda.Contiene(proyecto.CentroNombre, termino);
    }

    private string TextoConteo => HayFiltrosActivos
        ? Textos["ConteoConFiltro", ProyectosVisibles.Count, _proyectos.Count].Value
        : Textos["ConteoSinFiltro", _proyectos.Count].Value;

    private Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        NavigationManager.ActualizarFiltroEnUrl("q", valor);
        return Task.CompletedTask;
    }

    private Task CambiarEstadoAsync(string? valor)
    {
        _estadoFiltro = valor ?? string.Empty;
        NavigationManager.ActualizarFiltroEnUrl("estado", valor);
        return Task.CompletedTask;
    }

    private Task QuitarBusquedaAsync() => BuscarAsync(string.Empty);

    /// <summary>
    /// Quita los dos filtros, y los dos TAMBIÉN de la URL en una sola
    /// navegación: <see cref="OnParametersSetAsync"/> re-sincroniza desde la URL,
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
        _instantanea.Fijar(ValoresFormulario());
    }

    private readonly InstantaneaFormulario _instantanea = new();
    private readonly InstantaneaFormulario _instantaneaCierre = new();

    private readonly InstantaneaFormulario _instantaneaEdicionInfo = new();
    private readonly InstantaneaFormulario _instantaneaTecnico = new();

    /// <summary>
    /// P1-E2b: «hay cambios» del aviso de la página: el drawer de nuevo proyecto y la edición de la información o el alta de
    /// técnico del panel de detalle, comparados con cómo se abrieron (la fecha de hoy que traen puesta no es un cambio). Lo
    /// lee AvisoCambiosSinGuardar; cerrados (también tras guardar) nunca hay nada que perder. El modal de cerrar proyecto
    /// (ModalFormulario) lleva el suyo en <see cref="HayCambiosEnElModalDeCierre"/> y su propio aviso de navegación; cada
    /// drawer o modal pregunta solo por su propio contenido (HayCambiosEnElDrawer, HayCambiosEnElModalDeCierre).
    /// </summary>
    private bool HayCambiosSinGuardar =>
        HayCambiosEnElDrawer || HayCambiosEnElDetalle;

    private bool HayCambiosEnElDrawer => _drawerVisible && _instantanea.Difiere(ValoresFormulario());

    private bool HayCambiosEnElModalDeCierre => _mostrarCerrarConfirm && _instantaneaCierre.Difiere(_fechaCierre);

    /// <summary>
    /// La edición de la información y el alta de técnico del panel de detalle: cerrar el panel,
    /// abrir otro proyecto o cambiar de Cliente empresarial los descartan sin navegar, así que
    /// esas salidas preguntan con el mismo aviso (ConfirmarAbandonoAsync del ámbito).
    /// </summary>
    private bool HayCambiosEnElDetalle =>
        (_editandoInfo && _instantaneaEdicionInfo.Difiere(ValoresEdicionInfo()))
        || (_mostrarFormularioTecnico && _instantaneaTecnico.Difiere(ValoresTecnico()));

    private object?[] ValoresEdicionInfo() => [_editNombre, _editFechaFinPrevista, _editNotas];

    private object?[] ValoresTecnico() => [_nuevoTecnicoTrabajadorId, _nuevoTecnicoFechaAlta];

    /// <summary>
    /// El panel de detalle como zona que se desmonta sin navegar: cerrarlo, abrir otro
    /// proyecto o cambiar de Cliente empresarial preguntan con el aviso antes de hacerlo.
    /// </summary>
    private readonly AmbitoCambiosSinGuardar _ambitoDetalle = new();

    /// <summary>Cierra el panel de detalle (la X) preguntando antes si hay algo escrito que se perdería.</summary>
    private async Task CerrarDetalleConAvisoAsync()
    {
        if (!await _ambitoDetalle.ConfirmarAbandonoAsync()) return;
        CerrarDetalle();
    }

    /// <summary>
    /// Cerrar, reabrir o eliminar el proyecto cuyo detalle está abierto recarga o cierra el panel y tira lo que
    /// haya a medias en él (edición de información, alta de técnico): se pregunta antes de pedir la confirmación
    /// propia de la acción. Sobre otra fila no se pierde nada del panel, así que no pregunta.
    /// </summary>
    private async Task<bool> ConfirmarQueNoSePierdeElDetalleAsync(Guid idDelProyecto) =>
        idDelProyecto != _proyectoSeleccionadoId
        || !HayCambiosEnElDetalle
        || await _ambitoDetalle.ConfirmarAbandonoAsync();

    /// <summary>Abre el detalle de otro proyecto preguntando antes si el panel actual tiene cambios sin guardar.</summary>
    private async Task AbrirDetalleConAvisoAsync(Guid id)
    {
        if (!await _ambitoDetalle.ConfirmarAbandonoAsync()) return;
        await SeleccionarProyectoAsync(id);
    }

    /// <summary>
    /// Clic en cualquier punto de la fila. Sobre el proyecto que ya está abierto no hace nada: la
    /// fila entera es una diana grande, y un clic de más no debe devolver el panel a «Información»
    /// ni sacarlo de la edición.
    /// </summary>
    private Task AbrirDetalleDesdeLaFilaAsync(Guid id) =>
        _proyectoSeleccionadoId == id && _detalle is not null ? Task.CompletedTask : AbrirDetalleConAvisoAsync(id);

    /// <summary>
    /// Un técnico de la ventana de contexto del recuento: abre el panel del proyecto en la pestaña
    /// «Técnicos», que es donde se le da de baja o se asigna otro.
    /// </summary>
    private async Task AbrirDetalleEnTecnicosAsync(Guid id)
    {
        if (!await _ambitoDetalle.ConfirmarAbandonoAsync()) return;

        if (_proyectoSeleccionadoId != id || _detalle is null)
            await SeleccionarProyectoAsync(id);
        if (_proyectoSeleccionadoId != id || _detalle is null) return;

        CancelarEdicionInfo();
        _mostrarFormularioTecnico = false;
        await CambiarPestanaDetalleAsync("tecnicos");
    }

    [CascadingParameter] private Task<AuthenticationState>? EstadoAutenticacion { get; set; }

    /// <summary>
    /// Tecla «e»: el panel del proyecto, ya en edición (lo mismo que su lápiz). A quien no puede
    /// escribir se le abre en lectura: el lápiz tampoco se le ofrece. Si ese proyecto ya se está
    /// editando, no se toca lo escrito; lo demás que haya a medias en el panel (otro proyecto en
    /// edición, un alta de técnico) se pregunta antes de tirarlo.
    /// </summary>
    private async Task AbrirDetalleEnEdicionAsync(Guid id)
    {
        if (_proyectoSeleccionadoId == id && _editandoInfo) return;

        var mismoProyecto = _proyectoSeleccionadoId == id && _detalle is not null;
        if (mismoProyecto && !await SoloConEscritura.PuedeEscribirAsync(EstadoAutenticacion)) return;
        if (!await _ambitoDetalle.ConfirmarAbandonoAsync()) return;
        if (!mismoProyecto)
            await SeleccionarProyectoAsync(id);

        if (_proyectoSeleccionadoId != id || _detalle is null) return;
        if (!await SoloConEscritura.PuedeEscribirAsync(EstadoAutenticacion)) return;

        _mostrarFormularioTecnico = false;
        IniciarEdicionInfo();
    }

    private object?[] ValoresFormulario() => [_nuevoCentroId, _nuevoNombre, _nuevaFechaInicio, _nuevaFechaFinPrevista, _nuevasNotas];

    private void CerrarFormulariosDescartando()
    {
        _drawerVisible = false;
        _mostrarFormularioTecnico = false;
        CancelarEdicionInfo();
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
                _mensajeErrorFormulario = Textos["ErrorFaltaCentro"];
                return;
            }

            if (!DateOnly.TryParse(_nuevaFechaInicio, out var fechaInicio))
            {
                _mensajeErrorFormulario = Textos["ErrorFechaInicio"];
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

            ToastService.Mostrar(Textos["ToastCreado"], TonoToast.Exito);
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
            _mensajeErrorFormulario = Textos["ErrorCrear"];
        }
        finally
        {
            _guardando = false;
        }
    }

    // ---- Atajos de lista j/k/Enter (I-8 de AUDITORIA-USUARIO-AVANZADO-POST-GEN2) ----

    /// <summary>
    /// Fila enfocada por teclado. Es distinta de
    /// <see cref="_proyectoSeleccionadoId"/>: el foco recorre la lista sin
    /// abrir nada, y solo <c>Enter</c> lo convierte en selección.
    /// </summary>
    private Guid? _idEnfocado;

    private string ClaseFila(ProyectoListaDto proyecto)
    {
        var seleccionada = _proyectoSeleccionadoId == proyecto.Id;
        var enfocada = _idEnfocado == proyecto.Id;
        return (seleccionada, enfocada) switch
        {
            (true, true) => "fila-seleccionada fila-enfocada",
            (true, false) => "fila-seleccionada",
            (false, true) => "fila-enfocada",
            _ => string.Empty
        } + " fila-pulsable";
    }

    private int IndiceEnfocado(IReadOnlyList<ProyectoListaDto> visibles)
    {
        if (_idEnfocado is null) return -1;
        for (var i = 0; i < visibles.Count; i++)
            if (visibles[i].Id == _idEnfocado) return i;

        // Red de seguridad: el foco de una fila que ya no se ve se descarta en
        // OnParametersSet, pero si por cualquier camino sobreviviera, aquí se
        // trata como si no hubiera foco en vez de apuntar a algo invisible.
        return -1;
    }

    private async Task ManejarAtajoAsync(string tecla)
    {
        var visibles = ProyectosVisibles;

        // «e»: el lápiz del panel sobre la fila enfocada; sin fila enfocada, sobre el proyecto
        // cuyo panel está abierto.
        if (tecla == "e")
        {
            var idEditar = _idEnfocado is not null && IndiceEnfocado(visibles) >= 0 ? _idEnfocado : _proyectoSeleccionadoId;
            if (idEditar is { } id)
                await AbrirDetalleEnEdicionAsync(id);
            return;
        }

        if (visibles.Count == 0) return;

        switch (tecla)
        {
            case "j":
                _idEnfocado = visibles[Math.Min(IndiceEnfocado(visibles) + 1, visibles.Count - 1)].Id;
                break;
            case "k":
                {
                    var indice = IndiceEnfocado(visibles);
                    _idEnfocado = visibles[indice <= 0 ? 0 : indice - 1].Id;
                    break;
                }
            case "Enter":
                // § 6.1 quater del contrato: abrir es lo que hace el botón con
                // el nombre del proyecto, no el enlace de la celda "Centro" —
                // aquí se llama al mismo método que ese botón, así que el bug
                // histórico del enlace equivocado no puede reaparecer.
                if (_idEnfocado is { } idAbrir && IndiceEnfocado(visibles) >= 0)
                {
                    await AbrirDetalleConAvisoAsync(idAbrir);
                    return;
                }
                break;

                // "x" no tiene efecto en Proyectos y es deliberado: la pantalla no
                // tiene selección múltiple —ni casillas, ni BarraAccionesLote—, así
                // que no hay nada que marcar. Darle un significado nuevo sería una
                // decisión de producto, no la reparación de este hueco (mismo caso
                // que I-12 en Estado Comercial).
        }

        StateHasChanged();
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
        _mostrarFormularioTecnico = false;
        _mostrarCerrarConfirm = false;
    }

    private Task CambiarPestanaDetalleAsync(string pestana)
    {
        _pestanaDetalle = pestana;

        if (pestana == "tecnicos" && _detalle is not null && _tecnicos.Count == 0 && !_cargandoTecnicos)
            return CargarTecnicosAsync();

        return Task.CompletedTask;
    }

    private static int? DiasAbiertos(DateOnly inicio, DateOnly? cierre, DateOnly hoy) =>
        PlazoProyecto.DiasAbiertos(inicio, cierre, hoy);

    private string TextoTecnicosActivos(int tecnicosActivos) =>
        tecnicosActivos == 1
            ? Textos["TecnicosActivosUno", tecnicosActivos].Value
            : Textos["TecnicosActivosVarios", tecnicosActivos].Value;

    private string MetaTecnico(TecnicoProyectoDto tecnico)
    {
        var partes = new List<string>();
        partes.Add(Textos["MetaAltaFecha", tecnico.FechaAlta]);
        if (tecnico.FechaBaja is { } baja)
            partes.Add(Textos["MetaBajaFecha", baja]);
        return string.Join(" · ", partes);
    }

    // ---- Editar información ----

    private bool _editandoInfo;
    private string _editNombre = string.Empty;
    private string _editFechaFinPrevista = string.Empty;
    private string _editNotas = string.Empty;
    private Dictionary<string, string> _editErrores = new();
    private string? _editError;

    /// <summary>El lápiz de la cabecera del panel (y la tecla «e»): lleva a la pestaña Información, que es la que se edita.</summary>
    private void IniciarEdicionInfo()
    {
        _pestanaDetalle = "informacion";
        _editandoInfo = true;
        _instantaneaEdicionInfo.Fijar(ValoresEdicionInfo());
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

            ToastService.Mostrar(Textos["ToastActualizado"], TonoToast.Exito);
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
            _editError = Textos["ErrorGuardar"];
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

    private async Task AbrirCerrarConfirmAsync(Guid id)
    {
        if (!await ConfirmarQueNoSePierdeElDetalleAsync(id)) return;
        _idACerrar = id;
        _fechaCierre = Hoy.ToString("yyyy-MM-dd");
        _errorCierre = null;
        _mostrarCerrarConfirm = true;
        _instantaneaCierre.Fijar(_fechaCierre);
    }

    private async Task ConfirmarCerrarAsync()
    {
        if (_idACerrar is not { } idACerrar) return;

        if (!DateOnly.TryParse(_fechaCierre, out var fechaCierre))
        {
            _errorCierre = Textos["ErrorFechaCierre"];
            return;
        }

        _guardando = true;
        _errorCierre = null;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new CerrarProyectoCommand(idACerrar, fechaCierre));

            if (resultado.EsFallido)
            {
                _errorCierre = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar(Textos["ToastCerrado"], TonoToast.Exito);
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

    // ---- Reabrir proyecto ----

    /// <summary>
    /// Salida del cierre (FS-12): un cierre con la fecha equivocada afecta a la
    /// facturación por días, así que el proyecto cerrado ofrece «Reabrir». Pide
    /// confirmación nombrando la fecha de cierre, porque reabrir la borra y volver
    /// a cerrar propone la de hoy, no la anterior.
    /// </summary>
    private bool _confirmarReabrirVisible;
    private Guid _idAReabrir;
    private string _nombreAReabrir = string.Empty;
    private DateOnly? _fechaCierreAReabrir;
    private bool _reabriendo;

    private async Task AbrirReabrirConfirmAsync(Guid id)
    {
        if (!await ConfirmarQueNoSePierdeElDetalleAsync(id)) return;
        var fila = _proyectos.FirstOrDefault(p => p.Id == id);
        _idAReabrir = id;
        _nombreAReabrir = fila?.Nombre ?? _detalle?.Nombre ?? string.Empty;
        _fechaCierreAReabrir = fila?.FechaCierreReal ?? _detalle?.FechaCierreReal;
        _confirmarReabrirVisible = true;
    }

    private async Task ConfirmarReabrirAsync()
    {
        if (_reabriendo) return;
        _reabriendo = true;
        var id = _idAReabrir;

        try
        {
            var resultado = await Mediator.Send(new ReabrirProyectoCommand(id));

            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            ToastService.Mostrar(Textos["ToastReabierto"], TonoToast.Exito);
            _confirmarReabrirVisible = false;
            await CargarProyectosAsync();

            if (_detalle is not null && _detalle.Id == id)
                await SeleccionarProyectoAsync(_detalle.Id);
        }
        finally
        {
            _reabriendo = false;
        }
    }

    // ---- Eliminar proyecto ----

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _nombreAEliminar = string.Empty;
    private bool _eliminando;

    private async Task AbrirEliminarAsync(Guid id, string nombre)
    {
        if (!await ConfirmarQueNoSePierdeElDetalleAsync(id)) return;
        _idAEliminar = id;
        _nombreAEliminar = nombre;
        _confirmarEliminarVisible = true;
    }

    private async Task ConfirmarEliminarAsync()
    {
        _eliminando = true;

        try
        {
            var idEliminado = _idAEliminar;
            var resultado = await Mediator.Send(new EliminarProyectoCommand(idEliminado));

            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            ToastService.Mostrar(Textos["ToastEliminado"], TonoToast.Exito, Textos["ToastAccionDeshacer"], () => DeshacerEliminarAsync(idEliminado));
            _confirmarEliminarVisible = false;

            if (_proyectoSeleccionadoId == idEliminado)
                CerrarDetalle();

            await CargarProyectosAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["ErrorEliminar"], TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }

    /// <summary>«Deshacer» del aviso tras eliminar — ver RestaurarProyectoCommand.</summary>
    private async Task DeshacerEliminarAsync(Guid id)
    {
        // Guarda por elemento: dos pulsaciones en «Deshacer» del mismo aviso no mandan dos restauraciones.
        if (!_restaurando.Add(id)) return;

        try
        {
            var resultado = await Mediator.Send(new RestaurarProyectoCommand(id));

            ToastService.Mostrar(
                resultado.EsExitoso ? Textos["ToastRestaurado"].Value : resultado.Error.Mensaje,
                resultado.EsExitoso ? TonoToast.Exito : TonoToast.Error);

            if (resultado.EsExitoso)
                await CargarProyectosAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["ErrorRestaurar"], TonoToast.Error);
        }
        finally
        {
            _restaurando.Remove(id);
        }
    }

    private readonly HashSet<Guid> _restaurando = [];

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
            _trabajadoresDisponibles = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery(AlcanceSelectorTrabajadores.Cartera));

        _nuevoTecnicoTrabajadorId = string.Empty;
        _nuevoTecnicoFechaAlta = Hoy.ToString("yyyy-MM-dd");
        _errorTecnico = null;
        _mostrarFormularioTecnico = true;
        _instantaneaTecnico.Fijar(ValoresTecnico());
    }

    private async Task AsignarTecnicoAsync()
    {
        if (_detalle is null) return;

        if (!Guid.TryParse(_nuevoTecnicoTrabajadorId, out var trabajadorId))
        {
            _errorTecnico = Textos["ErrorFaltaTecnico"];
            return;
        }

        if (!DateOnly.TryParse(_nuevoTecnicoFechaAlta, out var fechaAlta))
        {
            _errorTecnico = Textos["ErrorFechaAlta"];
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

            ToastService.Mostrar(Textos["ToastTecnicoAsignado"], TonoToast.Exito);
            _mostrarFormularioTecnico = false;
            await CargarTecnicosAsync();
        }
        finally
        {
            _guardando = false;
        }
    }

    // Dar de baja a un técnico lo saca de la facturación por días del proyecto
    // y la aplicación no lo deshace: se confirma antes, como eliminar un proyecto.
    private TecnicoProyectoDto? _tecnicoADarDeBaja;
    private bool _dandoDeBajaTecnico;

    private string MensajeConfirmarBajaTecnico => _tecnicoADarDeBaja is null
        ? string.Empty
        : Textos["ConfirmarBajaTecnicoMensaje", _tecnicoADarDeBaja.TrabajadorNombreCompleto];

    private void PedirDarDeBajaTecnico(TecnicoProyectoDto tecnico) => _tecnicoADarDeBaja = tecnico;

    private void CerrarConfirmacionBajaTecnico(bool visible)
    {
        if (!visible && !_dandoDeBajaTecnico)
            _tecnicoADarDeBaja = null;
    }

    private async Task ConfirmarBajaTecnicoAsync()
    {
        if (_tecnicoADarDeBaja is not { } tecnico)
            return;

        _dandoDeBajaTecnico = true;
        try
        {
            await DesasignarTecnicoAsync(tecnico.Id);
        }
        finally
        {
            _dandoDeBajaTecnico = false;
            _tecnicoADarDeBaja = null;
        }
    }

    private async Task DesasignarTecnicoAsync(Guid id)
    {
        try
        {
            var resultado = await Mediator.Send(new DesasignarTecnicoProyectoCommand(id, Hoy));

            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            ToastService.Mostrar(Textos["ToastTecnicoDeBaja"], TonoToast.Exito);
            await CargarTecnicosAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["ErrorDarDeBaja"], TonoToast.Error);
        }
    }
}
