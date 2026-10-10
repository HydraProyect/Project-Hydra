using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Commands.EliminarCentros;
using CaeManager.Application.Centros.Commands.RestaurarCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Centros.Queries.ObtenerEmpresasDeCentrosVisibles;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Domain.Centros;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using CaeManager.Web.Features.Clientes.Components;
using CaeManager.Web.Features.Empresas.Components;
using FluentValidation;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Centros.Pages;

public partial class Centros : CaeManager.Web.Components.PaginaInteractiva
{
    /// <summary>Quien mira no alcanza nada en este Tenant (<see cref="CaeManager.Web.Features.IncorporacionCartera.Components.VacioSegunAlcance"/>):
    /// sin «+ Nuevo» en cabecera, para no duplicar lo que quizá ya existe fuera de su cartera.</summary>
    private bool _alcanceCero;

    // QuickGrid no soporta filas expandibles (Centro 360, Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md
    // § 0.1): cada Centro es una tarjeta con SeccionColapsable anidada, así
    // que la paginación se gestiona a mano en vez de con QuickGrid+Paginator
    // — la Query sigue paginando en servidor, solo cambia el control visual
    // (mismo PaginadorSimple que Usuarios.razor).
    private const int TamanoPaginaMinimo = 20;
    private int _tamanoPagina = TamanoPaginaMinimo;

    private string _busqueda = string.Empty;
    private string _estadoFiltro = string.Empty;

    /// <summary>Centros por estado para la franja, sin el filtro de estado aplicado. <c>null</c> hasta la primera carga.</summary>
    private IReadOnlyDictionary<string, int>? _recuentosPorEstado;

    /// <summary>Pastilla «Cliente»: Id del Cliente empresarial o vacío. Viaja en la URL como <c>cliente</c>.</summary>
    private string _clienteFiltro = string.Empty;

    /// <summary>«Empresa» de «Más filtros»: Id de la Empresa o vacío. Viaja en la URL como <c>empresa</c>.</summary>
    private string _empresaFiltro = string.Empty;

    private IReadOnlyList<OpcionEstado> _opcionesCliente = [];
    private IReadOnlyList<OpcionEstado> _opcionesEmpresa = [];

    /// <summary>
    /// Las opciones de las pastillas ya se pidieron (con éxito o no). Hasta entonces un filtro llegado
    /// por la URL no tiene nombre que pintar en su chip, y «—» diría que no existe.
    /// </summary>
    private bool _opcionesFiltroCargadas;

    /// <summary>Agrupación visual por Cliente empresarial: de serie, sí.</summary>
    private bool _agruparPorCliente = true;

    /// <summary>
    /// Grupos abiertos, por Id del Cliente empresarial. Sobrevive a las recargas (el Id es estable
    /// entre páginas y filtros) y se vacía al cambiar la agrupación.
    /// </summary>
    private readonly HashSet<Guid> _gruposAbiertos = [];
    private Guid? _centroIdFiltro;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;
    private int _pagina = 1;

    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_totalElementos / (double)_tamanoPagina));

    private IReadOnlyDictionary<Guid, IReadOnlyList<VisitaResumenDto>> _visitasPorCentro = new Dictionary<Guid, IReadOnlyList<VisitaResumenDto>>();

    private IReadOnlyList<ClienteSelectorDto> _clientesDisponibles = [];
    private IReadOnlyList<EmpresaSelectorDto> _empresasDisponibles = [];

    private bool _drawerVisible;
    private string _clienteId = string.Empty;
    private string _empresaId = string.Empty;
    // Cliente/Empresa llegaron ya fijados desde el encadenado de otra
    // pantalla (Fase A2) — se muestran en solo lectura hasta que el usuario
    // pulse "cambiar".
    private bool _padresFijadosPorCadena;
    private string _clienteNombreSoloLectura = string.Empty;
    private string _empresaNombreSoloLectura = string.Empty;
    private string _nombre = string.Empty;
    private string _codigoCentro = string.Empty;
    private string _direccion = string.Empty;
    private string _contacto = string.Empty;
    private string _contratoVigenteHasta = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private readonly HashSet<Guid> _seleccionados = [];

    /// <summary>
    /// Los checkboxes de fila solo se pintan con esto activo (§ 0.9) — son
    /// ruido permanente para una acción ocasional.
    /// </summary>
    private bool _seleccionMultiple;

    /// <summary>
    /// Qué filas tienen el acordeón abierto. La expansión la lleva la página
    /// y no cada <c>SeccionColapsable</c> con su estado interno, porque
    /// "Expandir/Colapsar todos" necesita poder decidirlo desde fuera.
    /// </summary>
    private readonly HashSet<Guid> _expandidos = [];
    private List<CentroListaDto> _elementosPagina = [];
    private Guid? _idEnfocado;
    private bool _eliminandoLote;
    // La baja de un Centro se hace solo desde la selección múltiple (patrón de listados: la fila
    // no lleva menú «⋯»): ☑ de la cabecera o tecla «x», «Eliminar seleccionados» y su «Deshacer».
    private bool _confirmarEliminarLoteVisible;

    // Crear inline desde el propio selector (Fase A4): si el Cliente o la
    // Empresa que hace falta no existen todavía, no hay que abandonar el
    // Drawer para ir a darlos de alta.
    private bool _formularioRapidoClienteVisible;
    private string _nombreParaCrearCliente = string.Empty;
    private bool _formularioRapidoEmpresaVisible;
    private string _nombreParaCrearEmpresa = string.Empty;

    private FormularioRapidoCliente? _formularioRapidoCliente;
    private FormularioRapidoEmpresa? _formularioRapidoEmpresa;

    private readonly InstantaneaFormulario _instantanea = new();

    /// <summary>
    /// El drawer de alta de Centro comparado con cómo se abrió (con lo que trajo la URL ya
    /// puesto). Lo lee el kit DrawerFormulario (S12, lote 2b): su aviso y el de la página
    /// se coordinan, así que salir con cambios en el drawer y en un modal rápido pregunta
    /// una sola vez (P1-E2b). Lee _drawerVisible en vivo: el kit recibe el Visible nuevo
    /// un render después.
    /// </summary>
    private bool HayCambiosCentro => _drawerVisible && _instantanea.Difiere(ValoresFormulario());

    /// <summary>
    /// Algo escrito en el modal de crear Cliente o Empresa abierto encima del drawer. Los
    /// modales rápidos no montan su propio aviso: los cubre el de la página.
    /// </summary>
    private bool HayCambiosFormulariosRapidos =>
        (_formularioRapidoClienteVisible && _formularioRapidoCliente?.HayCambiosSinGuardar == true)
        || (_formularioRapidoEmpresaVisible && _formularioRapidoEmpresa?.HayCambiosSinGuardar == true);

    private const int IndiceClienteEnValores = 0;
    private const int IndiceEmpresaEnValores = 1;

    private object?[] ValoresFormulario() =>
        [_clienteId, _empresaId, _nombre, _codigoCentro, _direccion, _contacto, _contratoVigenteHasta];

    private void FijarInstantaneaFormulario() => _instantanea.Fijar(ValoresFormulario());

    /// <summary>
    /// Lo escrito en un modal rápido solo repinta el modal. Este manejador existe para que
    /// su EventCallback repinte también la página, y el aviso del navegador al recargar o
    /// cerrar la pestaña, que se decide en el render, vea los cambios del modal.
    /// </summary>
    private void AlCambiarFormularioRapido()
    {
    }

    private void CerrarFormulariosDescartando()
    {
        _drawerVisible = false;
        _formularioRapidoClienteVisible = false;
        _formularioRapidoEmpresaVisible = false;
    }

    private IReadOnlyList<OpcionBuscable> ClientesComoOpciones =>
        _clientesDisponibles.Select(c => new OpcionBuscable(c.Id.ToString(), c.RazonSocial)).ToList();

    private IReadOnlyList<OpcionBuscable> EmpresasComoOpciones =>
        _empresasDisponibles.Select(e => new OpcionBuscable(e.Id.ToString(), e.RazonSocial)).ToList();

    private Guid? ClienteIdParaFormularioRapido => Guid.TryParse(_clienteId, out var id) ? id : null;

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    [SupplyParameterFromQuery(Name = "estado")]
    public string? EstadoInicial { get; set; }

    [SupplyParameterFromQuery(Name = "cliente")]
    public string? ClienteFiltroInicial { get; set; }

    [SupplyParameterFromQuery(Name = "empresa")]
    public string? EmpresaFiltroInicial { get; set; }

    /// <summary>
    /// Agrupación por Cliente empresarial. Nace activa: en la URL solo viaja
    /// cuando se quita (<c>?agrupar=no</c>), para que la dirección sin
    /// parámetros siga siendo la vista de fábrica.
    /// </summary>
    [SupplyParameterFromQuery(Name = "agrupar")]
    public string? AgruparInicial { get; set; }

    /// <summary>Orden pedido: <c>cumplimiento</c> (peores primero) o <c>cumplimiento-desc</c>. Sin él, el orden de catálogo.</summary>
    [SupplyParameterFromQuery(Name = "orden")]
    public string? OrdenInicial { get; set; }

    private const string AgruparNoEnUrl = "no";
    private const string OrdenCumplimientoEnUrl = "cumplimiento";
    private const string OrdenCumplimientoDescendenteEnUrl = "cumplimiento-desc";

    private (string? OrdenarPor, bool Descendente) OrdenDesdeUrl() => OrdenDesde(OrdenInicial);

    private static (string? OrdenarPor, bool Descendente) OrdenDesde(string? valor) => valor switch
    {
        OrdenCumplimientoEnUrl => (nameof(CentroListaDto.CumplimientoPorcentaje), false),
        OrdenCumplimientoDescendenteEnUrl => (nameof(CentroListaDto.CumplimientoPorcentaje), true),
        _ => (null, false),
    };

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ITenantActual TenantActual { get; set; } = default!;

    /// <summary>Estado 4a del mockup del selector: hay que elegir una empresa de la cartera antes de ver la lista.</summary>
    private bool _sinEmpresaSeleccionada;

    /// <summary>La empresa activa aún no se ha resuelto: se pinta una carga en vez de la lista.</summary>
    private bool _resolviendoEmpresa = true;

    /// <summary>
    /// Ciclo de vida de la página: se cancela al retirarla, para que la resolución de la empresa activa
    /// no trabaje para nadie ni repinte un componente ya desechado. Su <c>Token</c> se lee SIEMPRE antes
    /// del primer <c>await</c> (leerlo después, con un <see cref="Dispose"/> intermedio, lanzaría
    /// <see cref="ObjectDisposedException"/> donde nadie lo recoge).
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    public void Dispose()
    {
        if (_desechado)
            return;

        _desechado = true;
        WorkspaceService.OnEntidadGuardada -= AlGuardarEntidad;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    // Reutilizan las mismas reglas que ya corren en el servidor al guardar
    // (misma validación, sin duplicarla) — solo se les pide que validen un
    // único campo, no el Command completo, porque el resto del formulario
    // puede seguir a medio rellenar mientras el usuario todavía está en él.
    [Inject] private IValidator<CrearCentroCommand> ValidadorCrear { get; set; } = default!;

    /// <summary>Comando del palette "Crear centro «nombre»" (P3-31): abre el Drawer con el nombre precargado.</summary>
    [SupplyParameterFromQuery] public string? Accion { get; set; }
    [SupplyParameterFromQuery] public string? Nombre { get; set; }

    /// <summary>
    /// Encadenado desde "Continuar con el centro" en /empresas, o desde el
    /// asistente de alta guiada (Fase A2/A3): el Cliente y la Empresa llegan
    /// ya elegidos, en solo lectura, para no repetir la búsqueda.
    /// </summary>
    [SupplyParameterFromQuery] public Guid? ClienteId { get; set; }
    [SupplyParameterFromQuery] public Guid? EmpresaId { get; set; }

    /// <summary>
    /// Drill-down desde el desplegable de Centros con actividad de una
    /// Empresa (Centro 360, Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.11) — filtro exacto por
    /// Id, no reutiliza <c>q</c> (texto libre) porque un nombre parecido
    /// entre Centros distintos haría el prefiltro ambiguo.
    /// </summary>
    [SupplyParameterFromQuery] public Guid? CentroId { get; set; }

    protected override async Task OnInitializedAsync()
    {
        WorkspaceService.OnEntidadGuardada += AlGuardarEntidad;

        // Hasta resolver la empresa activa no se monta la lista ni sus acciones: con la consulta en
        // vuelo el render saldría con «hay empresa» y lanzaría la carga (y la exportación) del origen.
        var token = _ciclo.Token;
        try
        {
            var contexto = await ContextoEmpresaActiva.ResolverAsync(Mediator, TenantActual, token);
            if (_desechado)
                return;

            _sinEmpresaSeleccionada = contexto.SinSeleccion;
        }
        finally
        {
            if (!_desechado)
                _resolviendoEmpresa = false;
        }

        // La página se retiró mientras se resolvía la empresa (p. ej. una dependencia que ignora el
        // token): ni carga ni estado nuevo sobre un componente desechado.
        if (_desechado)
            return;

        // Sin empresa elegida no se piden los datos de la organización de origen ni se abre el alta.
        if (_sinEmpresaSeleccionada)
            return;

        _busqueda = TerminoBusquedaInicial ?? string.Empty;
        _estadoFiltro = EstadoCentroUi.SeleccionValida(EstadoInicial);
        _clienteFiltro = IdDesdeUrl(ClienteFiltroInicial);
        _empresaFiltro = IdDesdeUrl(EmpresaFiltroInicial);
        _centroIdFiltro = CentroId;
        (_ordenarPor, _ordenDescendente) = OrdenDesdeUrl();
        _agruparPorCliente = AgruparInicial != AgruparNoEnUrl;
        _puedeEscribir = await SoloConEscritura.PuedeEscribirAsync(EstadoAutenticacion);
        await CargarAsync();

        if (Accion == "crear")
        {
            await AbrirCrearAsync();
            if (!string.IsNullOrWhiteSpace(Nombre))
                _nombre = Nombre;

            // Referencia de partida con lo que trae la URL. Se toma aquí, antes del siguiente
            // await: el drawer ya está abierto y editable mientras cargan las Empresas, y lo
            // que se teclee en esa espera sí es un cambio. Después solo se le añaden Cliente
            // y Empresa prefijados.
            var alAbrir = ValoresFormulario();

            if (ClienteId is not null && _clientesDisponibles.Any(c => c.Id == ClienteId))
            {
                _clienteId = ClienteId.Value.ToString();
                alAbrir[IndiceClienteEnValores] = _clienteId;
                _clienteNombreSoloLectura = _clientesDisponibles.First(c => c.Id == ClienteId).RazonSocial;
                await CargarEmpresasDisponiblesAsync(ClienteId);

                if (EmpresaId is not null && _empresasDisponibles.Any(e => e.Id == EmpresaId))
                {
                    _empresaId = EmpresaId.Value.ToString();
                    alAbrir[IndiceEmpresaEnValores] = _empresaId;
                    _empresaNombreSoloLectura = _empresasDisponibles.First(e => e.Id == EmpresaId).RazonSocial;
                    _padresFijadosPorCadena = true;
                }
            }

            _instantanea.Fijar(alAbrir);
        }

        // Las opciones de las pastillas «Cliente» y «Empresa», al final: la lista y el
        // alta encadenada no esperan por ellas.
        await CargarOpcionesDeFiltroAsync();
    }

    /// <summary>
    /// Se re-ejecuta en cada navegación dentro de la propia página (recargar,
    /// compartir la URL, volver atrás) — no solo en el primer render — para
    /// que el filtro de la URL sea la fuente de verdad, no solo su semilla
    /// inicial (P1-18 de Project-Hydra-Negocio/MATURITY_REVIEW.md). El primer paso
    /// (justo después de OnInitializedAsync) siempre coincide con lo que ya
    /// se cargó ahí, así que esto no duplica la primera consulta.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        // Mientras se resuelve la empresa, o con el 4a, la lista no se carga (ni por cambio de URL).
        if (_resolviendoEmpresa || _sinEmpresaSeleccionada)
            return;

        var deLaUrl = TerminoBusquedaInicial ?? string.Empty;
        var estadoDeLaUrl = EstadoCentroUi.SeleccionValida(EstadoInicial);
        var clienteDeLaUrl = IdDesdeUrl(ClienteFiltroInicial);
        var empresaDeLaUrl = IdDesdeUrl(EmpresaFiltroInicial);
        var (ordenarPorDeLaUrl, descendenteDeLaUrl) = OrdenDesdeUrl();

        // La agrupación es solo visual: se aplica sin volver a pedir la lista.
        AplicarAgrupacion(AgruparInicial != AgruparNoEnUrl);

        if (deLaUrl == _busqueda && estadoDeLaUrl == _estadoFiltro && clienteDeLaUrl == _clienteFiltro
            && empresaDeLaUrl == _empresaFiltro && CentroId == _centroIdFiltro
            && ordenarPorDeLaUrl == _ordenarPor && descendenteDeLaUrl == _ordenDescendente)
            return;

        _ordenarPor = ordenarPorDeLaUrl;
        _ordenDescendente = descendenteDeLaUrl;
        _busqueda = deLaUrl;
        _estadoFiltro = estadoDeLaUrl;
        _clienteFiltro = clienteDeLaUrl;
        _empresaFiltro = empresaDeLaUrl;
        _centroIdFiltro = CentroId;
        await CargarAsync(resetPagina: true);
    }

    /// <summary>
    /// Un Id de la URL solo filtra si es un Guid: la coordenada viene de fuera. No se exige que esté
    /// entre las opciones —la consulta ya acota al alcance de quien mira, así que uno ajeno devuelve
    /// una lista vacía, no datos de otro—; se normaliza para compararlo con el valor de la opción.
    /// </summary>
    private static string IdDesdeUrl(string? valor) =>
        Guid.TryParse(valor, out var id) ? id.ToString() : string.Empty;

    private static Guid? IdDeFiltro(string valor) => Guid.TryParse(valor, out var id) ? id : null;

    /// <summary>
    /// Opciones de las pastillas «Cliente» y «Empresa», las dos por el alcance de
    /// <b>visibilidad</b>, el mismo que la lista: los Clientes empresariales visibles y las Empresas de
    /// los Centros visibles. No el selector de Empresas del alta, que acota por alcance de gestión y para
    /// un usuario de portal (rol Cliente) va vacío aunque vea Centros con su Empresa. Cada una con su
    /// propio try/catch: si una falla, la otra pastilla sigue ofreciendo sus opciones, y la lista se
    /// pinta igual.
    /// </summary>
    private async Task CargarOpcionesDeFiltroAsync()
    {
        try
        {
            var clientes = await Mediator.Send(new ObtenerClientesParaSelectorQuery());
            _opcionesCliente = clientes.Select(c => new OpcionEstado(c.Id.ToString(), c.RazonSocial)).ToList();
        }
        catch (Exception)
        {
            _opcionesCliente = [];
        }

        try
        {
            var empresas = await Mediator.Send(new ObtenerEmpresasDeCentrosVisiblesQuery());
            _opcionesEmpresa = empresas.Select(e => new OpcionEstado(e.Id.ToString(), e.RazonSocial)).ToList();
        }
        catch (Exception)
        {
            _opcionesEmpresa = [];
        }

        _opcionesFiltroCargadas = true;
    }

    /// <summary>
    /// Nombre de la opción elegida para su chip: «…» mientras las opciones aún no han llegado y «—» si,
    /// ya cargadas, el Id no está entre ellas (un Id de la URL fuera del alcance de quien mira).
    /// </summary>
    private string TextoOpcion(IReadOnlyList<OpcionEstado> opciones, string valor) =>
        opciones.FirstOrDefault(o => o.Valor == valor)?.Texto ?? (_opcionesFiltroCargadas ? "—" : "…");

    /// <summary>La consulta de la página que se está viendo: filtros, orden y paginación actuales.</summary>
    private ObtenerCentrosQuery ConsultaDePaginaActual() => new(
        Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
        ClienteId: IdDeFiltro(_clienteFiltro),
        Estado: null,
        OrdenarPor: _ordenarPor,
        Descendente: _ordenDescendente,
        Pagina: _pagina,
        TamanoPagina: _tamanoPagina,
        CentroId: _centroIdFiltro,
        EmpresaId: IdDeFiltro(_empresaFiltro),
        Estados: SeleccionEstados.Separar<EstadoCentro>(_estadoFiltro) is { Count: > 0 } estados ? estados : null,
        ConRecuentosPorEstado: true);

    private async Task CargarAsync(bool resetPagina = false)
    {
        if (resetPagina)
            _pagina = 1;

        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(ConsultaDePaginaActual());

            _totalElementos = resultado.TotalElementos;
            _recuentosPorEstado = resultado.RecuentosPorEstado;
            _elementosPagina = resultado.Elementos.ToList();
            _seleccionados.Clear();
            _expandidos.Clear();
            _idEnfocado = null;

            // Batch por página (no por fila) — mismo criterio que el badge de
            // cumplimiento: evita N+1 aunque solo se pinte cuando hay visita.
            _visitasPorCentro = _elementosPagina.Count == 0
                ? new Dictionary<Guid, IReadOnlyList<VisitaResumenDto>>()
                : await Mediator.Send(new ObtenerProximaVisitaPorCentroQuery(_elementosPagina.Select(c => c.Id).ToList()));
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            _cargando = false;
            StateHasChanged();
        }
    }

    private Task CambiarPaginaAsync(int pagina)
    {
        _pagina = pagina;
        return CargarAsync();
    }

    /// <summary>
    /// Tras guardar un documento in situ desde el acordeón (Gestionar, item 3
    /// del backlog Centro 360) hace falta refrescar el estado/recuentos de
    /// ESA fila — pero CargarAsync() completo colapsaría el acordeón que el
    /// usuario acaba de usar (limpia _expandidos). Un CentroId filtrado
    /// devuelve solo esa fila y se sustituye en sitio, sin tocar expansión ni
    /// selección ni paginación.
    /// </summary>
    private async Task RefrescarCentroAsync(Guid centroId)
    {
        var resultado = await Mediator.Send(new ObtenerCentrosQuery(Busqueda: null, ClienteId: null, CentroId: centroId));
        var actualizado = resultado.Elementos.FirstOrDefault();
        if (actualizado is null || _desechado) return;

        var indice = _elementosPagina.FindIndex(c => c.Id == centroId);
        if (indice >= 0)
            _elementosPagina[indice] = actualizado;

        StateHasChanged();
    }

    // ── La fila se refresca tras guardar en la vista rápida ─────────────────────────────────────
    // El panel vive en MainLayout y guarda sin pasar por esta página: avisa por
    // ContextWorkspaceService.OnEntidadGuardada. Se vuelve a pedir SOLO esa fila y se sustituye
    // en sitio con RefrescarCentroAsync: filtros, orden, página, selección, fila enfocada,
    // acordeones y desplazamiento no se tocan, y la fila permanece aunque el cambio la saque del
    // filtro activo, hasta la siguiente carga. La consulta por id no lleva ConRecuentosPorEstado
    // y no le hace falta: los dos caminos del handler calculan el estado y el cumplimiento de la
    // fila con el mismo servicio, así que sale igual que en la carga de página.

    private void AlGuardarEntidad(EntidadWorkspace tipo, Guid id)
    {
        if (tipo == EntidadWorkspace.Centro)
            _ = InvokeAsync(() => RefrescarFilaAsync(id));
    }

    private async Task RefrescarFilaAsync(Guid id)
    {
        // Con una carga en vuelo no se sustituye nada: la sustitución caería sobre una página que
        // está a punto de cambiar. Hueco conocido: si esa carga leyó antes de que el guardado
        // fuera firme, la fila conserva el dato anterior hasta la siguiente carga.
        if (_desechado || _cargando || !_elementosPagina.Any(c => c.Id == id))
            return;

        try
        {
            await RefrescarCentroAsync(id);
        }
        catch (Exception)
        {
            // El guardado ya es firme: que falle la relectura no es un error que enseñar. La
            // fila conserva el dato anterior hasta la siguiente carga, como antes de este aviso.
        }
    }

    private DrawerAsignacionMasiva _drawerAsignacion = default!;

    [CascadingParameter] private Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState>? EstadoAutenticacion { get; set; }

    /// <summary>
    /// El rol efectivo puede escribir (misma pregunta que <see cref="SoloConEscritura"/>). Decide si
    /// las incidencias de las ventanas del motivo («N vencidos», «M por vencer») se ofrecen como pulsables: a quien
    /// solo consulta no se le ofrece un formulario que el comando le va a denegar.
    /// </summary>
    private bool _puedeEscribir;

    [Inject] private Microsoft.Extensions.Localization.IStringLocalizer<CaeManager.Web.Recursos.TextosComunes> Comunes { get; set; } = default!;

    private CaeManager.Web.Features.Documentos.Components.CorreccionIncidenciaDocumental _correccion = default!;

    /// <summary>
    /// Las causas de Empresa no llevan su Empresa en la incidencia: es la del Centro. Las de
    /// Trabajador llevan su <c>TrabajadorId</c>. Devuelve la Empresa solo cuando la incidencia es suya.
    /// </summary>
    private static Guid? EmpresaDeIncidencia(CentroListaDto centro, IncidenciaCentroDto incidencia) =>
        incidencia.Ambito == AmbitoCausa.Empresa ? centro.EmpresaId : null;

    private bool EsCorregible(CentroListaDto centro, IncidenciaCentroDto incidencia) =>
        _puedeEscribir && CaeManager.Web.Features.Documentos.Components.CorreccionIncidenciaDocumental.EsCorregible(
            incidencia.DocumentoId, incidencia.TipoDocumentoId, incidencia.TrabajadorId, EmpresaDeIncidencia(centro, incidencia));

    private bool HayCorregibles(CentroListaDto centro, IReadOnlyList<IncidenciaCentroDto> incidencias) =>
        incidencias.Any(i => EsCorregible(centro, i));

    private string? PieDeIncidencias(CentroListaDto centro, IReadOnlyList<IncidenciaCentroDto> incidencias) =>
        HayCorregibles(centro, incidencias) ? Comunes["VentanaIncidenciasPie"].Value : null;

    private Task CorregirIncidenciaAsync(CentroListaDto centro, IncidenciaCentroDto incidencia) =>
        _correccion.AbrirAsync(
            incidencia.DocumentoId, incidencia.TipoDocumentoId, incidencia.TrabajadorId, EmpresaDeIncidencia(centro, incidencia));

    /// <summary>
    /// Tras corregir una incidencia desde la ventana de contexto. Un documento de Empresa cuenta en
    /// todos sus Centros y un Trabajador puede estar asignado a varios, así que no basta con
    /// refrescar la fila pulsada (<see cref="RefrescarCentroAsync"/>): se vuelve a pedir la página
    /// tal como está —mismos filtros, orden y página— y se sustituye en sitio. La selección se
    /// conserva; los acordeones se cierran porque su contenido ya no es el de antes de corregir.
    /// </summary>
    private async Task RefrescarTrasCorreccionAsync()
    {
        ResultadoPaginado<CentroListaDto> resultado;
        try
        {
            resultado = await Mediator.Send(ConsultaDePaginaActual());
        }
        catch (Exception)
        {
            // El documento ya se guardó: que falle la relectura no es un error del formulario.
            // CargarAsync enseña el estado de error de la lista, con su reintento.
            await CargarAsync();
            return;
        }

        _totalElementos = resultado.TotalElementos;
        _recuentosPorEstado = resultado.RecuentosPorEstado;
        _elementosPagina = resultado.Elementos.ToList();
        _seleccionados.IntersectWith(_elementosPagina.Select(c => c.Id));
        _expandidos.Clear();
        StateHasChanged();
    }

    /// <summary>
    /// Item 4 del backlog Centro 360: "Asignar a centros seleccionados…" en
    /// la barra de lote. A diferencia de RefrescarCentroAsync (una sola
    /// fila), el usuario puede añadir más centros dentro del propio drawer
    /// (la matriz no se recorta) — no hay un conjunto de filas afectadas
    /// conocido de antemano, así que se recarga la página completa, mismo
    /// criterio que ya usa "Eliminar seleccionados" en esta misma barra.
    /// </summary>
    private Task ManejarAsignacionMasivaGuardadaAsync()
    {
        _seleccionados.Clear();
        return CargarAsync();
    }

    // H5 (Project-Hydra-Negocio/tecnico/docs/ux-audit/05-trabajadores-vehiculos.md): selector de tamaño de página, compartido por PaginadorSimple.razor.
    private Task CambiarTamanoPaginaAsync(int tamano)
    {
        _tamanoPagina = tamano;
        return CargarAsync(resetPagina: true);
    }

    private async Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        NavigationManager.ActualizarFiltroEnUrl("q", valor);
        await CargarAsync(resetPagina: true);
    }

    private async Task CambiarEstadoAsync(string? valor)
    {
        _estadoFiltro = valor ?? string.Empty;
        NavigationManager.ActualizarFiltroEnUrl("estado", valor);
        await CargarAsync(resetPagina: true);
    }

    private async Task CambiarClienteFiltroAsync(string valor)
    {
        _clienteFiltro = valor;
        NavigationManager.ActualizarFiltroEnUrl("cliente", valor);
        await CargarAsync(resetPagina: true);
    }

    private async Task CambiarEmpresaFiltroAsync(string valor)
    {
        _empresaFiltro = valor;
        NavigationManager.ActualizarFiltroEnUrl("empresa", valor);
        await CargarAsync(resetPagina: true);
    }

    private bool HayFiltrosActivos =>
        !string.IsNullOrWhiteSpace(_busqueda) || !string.IsNullOrWhiteSpace(_estadoFiltro)
        || !string.IsNullOrWhiteSpace(_clienteFiltro) || !string.IsNullOrWhiteSpace(_empresaFiltro);

    /// <summary>
    /// «Limpiar todo»: quita los cuatro filtros <b>también de la URL</b>, en una sola llamada (cada
    /// <c>NavigateTo</c> lee la URL vigente y varias seguidas se pisan; ver el helper). Si la URL
    /// conservara alguno, <c>OnParametersSetAsync</c> —que re-sincroniza desde ella— lo devolvería.
    /// </summary>
    private async Task LimpiarFiltrosAsync()
    {
        _busqueda = string.Empty;
        _estadoFiltro = string.Empty;
        _clienteFiltro = string.Empty;
        _empresaFiltro = string.Empty;
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["q"] = null,
            ["estado"] = null,
            ["cliente"] = null,
            ["empresa"] = null,
        });
        await CargarAsync(resetPagina: true);
    }

    // ---- Filtros guardados (pieza compartida FiltrosGuardadosDeListado) ----

    private const string PantallaDeFiltrosGuardados =
        CaeManager.Application.Configuracion.Commands.GuardarFiltro.PantallasConFiltrosGuardados.Centros;

    /// <summary>
    /// Lista blanca de los parámetros de VISTA de la URL: lo que guarda y aplica un filtro guardado.
    /// Fuera quedan <c>accion</c>, las precargas del alta encadenada (<c>Nombre</c>, <c>ClienteId</c>,
    /// <c>EmpresaId</c>) y <c>CentroId</c>, que señala un Centro concreto y no es una vista.
    /// </summary>
    public static readonly IReadOnlyList<string> ParametrosDeVista = ["q", "estado", "cliente", "empresa", "agrupar", "orden"];

    private readonly ConexionFiltrosGuardados _filtrosGuardados = new();

    /// <summary>
    /// Un filtro guardado define la vista entera: lo que no trae se quita (sin <c>agrupar</c> vuelve la
    /// agrupación de fábrica; sin <c>orden</c>, el de catálogo). Cada valor pasa por la misma validación
    /// que el de la URL, y la URL se escribe en una sola navegación antes de recargar; así
    /// <see cref="OnParametersSetAsync"/> la encuentra igual que los campos y no repite la consulta.
    /// </summary>
    private async Task AplicarVistaGuardadaAsync(IReadOnlyDictionary<string, string?> vista)
    {
        _busqueda = vista.GetValueOrDefault("q") ?? string.Empty;
        _estadoFiltro = EstadoCentroUi.SeleccionValida(vista.GetValueOrDefault("estado"));
        _clienteFiltro = IdDesdeUrl(vista.GetValueOrDefault("cliente"));
        _empresaFiltro = IdDesdeUrl(vista.GetValueOrDefault("empresa"));
        (_ordenarPor, _ordenDescendente) = OrdenDesde(vista.GetValueOrDefault("orden"));
        AplicarAgrupacion(vista.GetValueOrDefault("agrupar") != AgruparNoEnUrl);

        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["q"] = _busqueda,
            ["estado"] = _estadoFiltro,
            ["cliente"] = _clienteFiltro,
            ["empresa"] = _empresaFiltro,
            ["agrupar"] = _agruparPorCliente ? null : AgruparNoEnUrl,
            ["orden"] = _ordenarPor is null
                ? null
                : _ordenDescendente ? OrdenCumplimientoDescendenteEnUrl : OrdenCumplimientoEnUrl,
        });
        await CargarAsync(resetPagina: true);
    }

    private bool MostrarPaginador =>
        _totalElementos > TamanoPaginaMinimo || (_totalElementos > 0 && _tamanoPagina > TamanoPaginaMinimo);

    // --- Agrupación visual por Cliente empresarial y filas tintadas (rediseño de listados, fase 1) ---

    /// <summary>Un grupo de la página: los Centros de un Cliente empresarial y su resumen.</summary>
    private sealed record GrupoCentros(Guid ClienteEmpresarialId, string Nombre, IReadOnlyList<CentroListaDto> Centros)
    {
        public int ConProblema => Centros.Count(c => NivelProblema(c) == 2);
        public int PorVencer => Centros.Count(c => NivelProblema(c) == 1);
        public int Peor => Centros.Max(NivelProblema);
    }

    /// <summary>
    /// El Cliente empresarial de la fila: único punto de la agrupación que lee el campo heredado del
    /// DTO (deuda terminológica: es el Id de la Empresa en posición Cliente empresarial).
    /// </summary>
    private static Guid ClienteEmpresarialDe(CentroListaDto centro) => centro.ClienteId;

    /// <summary>
    /// Grupos de la página. Con el orden de serie, primero el de peor estado (como la maqueta aprobada) y,
    /// a igual estado, en el orden en que aparece su primer Centro. Con un orden pedido (cumplimiento), en
    /// el orden en que aparece su primer Centro: el grupo de la primera fila pedida va primero y dentro de
    /// cada grupo se respeta ese orden, así que la agrupación no lo contradice (GroupBy conserva la primera
    /// aparición y OrderByDescending es estable).
    /// </summary>
    private IReadOnlyList<GrupoCentros> Grupos
    {
        get
        {
            var grupos = _elementosPagina
                .GroupBy(ClienteEmpresarialDe)
                .Select(g => new GrupoCentros(g.Key, g.First().ClienteRazonSocial, g.ToList()));
            return (_ordenarPor is null ? grupos.OrderByDescending(g => g.Peor) : grupos).ToList();
        }
    }

    /// <summary>Filas en el orden en que se pintan: agrupadas, las de cada grupo seguidas.</summary>
    private IEnumerable<CentroListaDto> FilasEnOrden(IReadOnlyList<GrupoCentros> grupos) =>
        _agruparPorCliente ? grupos.SelectMany(g => g.Centros) : _elementosPagina;

    /// <summary>Filas que se ven (fuera de los grupos contraídos): las que recorren j/k.</summary>
    private List<CentroListaDto> FilasVisibles() =>
        FilasEnOrden(Grupos).Where(c => GrupoAbierto(ClienteEmpresarialDe(c))).ToList();

    /// <summary>
    /// Un grupo se ve abierto si se abrió, y además siempre que haya búsqueda (lo buscado no se
    /// esconde), un Centro concreto en la URL (el enlace profundo lleva a él) o la selección múltiple
    /// esté activa: «Seleccionar los de esta página» y la barra de lote actúan sobre filas que se ven.
    /// </summary>
    private bool GrupoAbierto(Guid clienteEmpresarialId) =>
        GruposForzadosAbiertos || _gruposAbiertos.Contains(clienteEmpresarialId);

    /// <summary>
    /// Todos los grupos se ven abiertos por algo ajeno a ellos (sin agrupar, búsqueda, Centro en la URL,
    /// selección múltiple): su cabecera no puede contraerlos, así que no se pinta como botón.
    /// </summary>
    private bool GruposForzadosAbiertos =>
        !_agruparPorCliente
        || !string.IsNullOrWhiteSpace(_busqueda)
        || _centroIdFiltro is not null
        || _seleccionMultiple
        || _seleccionados.Count > 0;

    private void AlternarGrupo(Guid clienteEmpresarialId)
    {
        if (!_gruposAbiertos.Add(clienteEmpresarialId))
            _gruposAbiertos.Remove(clienteEmpresarialId);

        DesenfocarSiOculta();
    }

    /// <summary>
    /// La fila enfocada (j/k) deja de verse al contraer su grupo: se suelta el foco, para que x y Enter
    /// no actúen sobre un Centro que no está en pantalla (x lo marcaría para la baja en lote).
    /// </summary>
    private void DesenfocarSiOculta()
    {
        if (_idEnfocado is { } id && !FilasVisibles().Any(c => c.Id == id))
            _idEnfocado = null;
    }

    /// <summary>El conmutador de la barra: cambia la agrupación y la escribe en la URL.</summary>
    private void CambiarAgrupacion(bool agrupar)
    {
        AplicarAgrupacion(agrupar);
        NavigationManager.ActualizarFiltroEnUrl("agrupar", agrupar ? null : AgruparNoEnUrl);
    }

    private void AplicarAgrupacion(bool agrupar)
    {
        if (_agruparPorCliente == agrupar)
            return;

        _agruparPorCliente = agrupar;
        _gruposAbiertos.Clear();
        _idEnfocado = null;
    }

    private string ClaseTarjeta(CentroListaDto centro) =>
        "tarjeta-fila-acordeon"
        + (centro.Id == _idEnfocado ? " fila-enfocada" : string.Empty)
        + ClaseTinte(NivelProblema(centro));

    private static string ClaseTinteGrupo(GrupoCentros grupo) => ClaseTinte(grupo.Peor);

    private static string ClaseTinte(int nivel) => nivel switch
    {
        2 => " fila-tintada-peligro",
        1 => " fila-tintada-aviso",
        _ => string.Empty,
    };

    /// <summary>
    /// Fila con problema: 2 = bloqueo de la plataforma CAE, vencido o falta documentación
    /// (tinte de peligro, «con problema»); 1 = urgente (tinte de aviso, «por vencer»); 0 = el resto.
    /// Se lee del estado del Centro, el mismo que pinta su badge.
    /// </summary>
    private static int NivelProblema(CentroListaDto centro) => centro.Estado switch
    {
        EstadoCentro.Bloqueado or EstadoCentro.Vencido or EstadoCentro.Faltante => 2,
        EstadoCentro.Urgente => 1,
        _ => 0,
    };

    // --- Patrón único de lista (Project-Hydra-Negocio/tecnico/CONTRATO-PATRON-PANTALLA-LISTA-2026-09-28.md) ---

    /// <summary>
    /// Un clic en la fila, su nombre o Enter sobre la fila enfocada: la vista rápida (pieza 6). En
    /// Centros es el panel del Context Workspace, que ya existe; el contrato prohíbe sumarle un drawer.
    /// A la página Centro 360 (/centros/{id}) se va con el icono 360 de la fila y con el del panel.
    /// </summary>
    private Task AbrirPanelAsync(Guid id)
    {
        var centro = _elementosPagina.FirstOrDefault(e => e.Id == id);
        return centro is null
            ? Task.CompletedTask
            : WorkspaceService.AbrirAsync(EntidadWorkspace.Centro, centro.Id, centro.Nombre, "informacion");
    }

    /// <summary>
    /// Tecla «e»: la vista rápida de la fila enfocada, ya en edición (el lápiz de la cabecera
    /// del panel). Si el rol no puede escribir, el panel se abre y se queda en lectura.
    /// </summary>
    private Task AbrirPanelEnEdicionAsync(Guid id) =>
        WorkspaceService.AbrirEnEdicionAsync(EntidadWorkspace.Centro, id, NombreDe(id));

    // El Centro del panel puede no estar en la página (el filtro lo dejó fuera): su nombre es
    // entonces el del frame abierto.
    private string NombreDe(Guid id) =>
        _elementosPagina.FirstOrDefault(e => e.Id == id)?.Nombre
        ?? (WorkspaceService.FrameActual is { } frame && frame.EntidadId == id ? frame.TituloVisible : string.Empty);

    /// <summary>Centro cuyo panel está abierto arriba de la pila del Context Workspace, si lo hay.</summary>
    private Guid? CentroEnVistaRapida =>
        WorkspaceService.FrameActual is { Tipo: EntidadWorkspace.Centro } frame ? frame.EntidadId : null;

    private string EtiquetaFiltroBusqueda => Textos["ChipBusqueda", _busqueda].Value;


    /// <summary>«N centros»; con filtros, dice que el número es el de los que coinciden.</summary>
    private string TextoConteo
    {
        get
        {
            var uno = _totalElementos == 1;
            var clave = HayFiltrosActivos
                ? (uno ? "ConteoUnoFiltrado" : "ConteoVariosFiltrado")
                : (uno ? "ConteoUno" : "ConteoVarios");
            return Textos[clave, _totalElementos].Value;
        }
    }

    private async Task AbrirCrearAsync()
    {
        _clientesDisponibles = await Mediator.Send(new ObtenerClientesParaSelectorQuery());

        _clienteId = string.Empty;
        _empresaId = string.Empty;
        _padresFijadosPorCadena = false;
        _nombre = string.Empty;
        _codigoCentro = string.Empty;
        _direccion = string.Empty;
        _contacto = string.Empty;
        _contratoVigenteHasta = string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;

        // Sin Cliente elegido todavía no hay por qué acotar: se carga el
        // catálogo completo y CambiarClienteCreacionAsync lo recorta en
        // cuanto el usuario elige uno (Fase A3 — antes se cargaban siempre
        // todas las empresas de todos los clientes, aunque la Query ya sabía
        // filtrar por ClienteId).
        await CargarEmpresasDisponiblesAsync(null);

        _drawerVisible = true;
        FijarInstantaneaFormulario();
    }

    /// <summary>
    /// Recarga el selector de Empresa acotado al Cliente elegido — o al
    /// catálogo completo si todavía no hay Cliente. Se llama al abrir el
    /// Drawer y cada vez que el usuario cambia el Cliente en modo creación.
    /// </summary>
    private async Task CargarEmpresasDisponiblesAsync(Guid? clienteId)
    {
        _empresasDisponibles = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery(clienteId));
    }

    /// <summary>
    /// Solo aplica en modo creación con los padres editables (no fijados por
    /// una cadena de otra pantalla): al cambiar el Cliente, el selector de
    /// Empresa se recorta a las suyas — si la Empresa que estaba elegida ya
    /// no pertenece al nuevo Cliente, se limpia en vez de dejar una
    /// combinación imposible.
    /// </summary>
    private async Task CambiarClienteCreacionAsync(string valor)
    {
        _clienteId = valor;
        // Solo el aviso del Cliente: si al cambiar de Cliente la Empresa elegida se vacía (más abajo), su aviso sigue siendo verdad.
        if (_mensajeErrorFormulario == AvisoSeleccionaCliente)
            _mensajeErrorFormulario = null;

        var clienteId = Guid.TryParse(valor, out var id) ? id : (Guid?)null;
        await CargarEmpresasDisponiblesAsync(clienteId);

        if (!_empresasDisponibles.Any(e => e.Id.ToString() == _empresaId))
            _empresaId = string.Empty;
    }

    private const string AvisoSeleccionaCliente = "Selecciona un Cliente.";
    private const string AvisoSeleccionaEmpresa = "Selecciona una empresa.";

    private void AlElegirEmpresa(string valor)
    {
        _empresaId = valor;
        LimpiarAvisoDeSeleccionFaltante();
    }

    /// <summary>
    /// El aviso «Selecciona un Cliente.» (o «Selecciona una empresa.») es de un intento anterior de guardar: al rellenar el
    /// campo deja de ser verdad (misma regla que Trabajadores, #1020). Un error de servidor sigue en pantalla.
    /// </summary>
    private void LimpiarAvisoDeSeleccionFaltante()
    {
        if (_mensajeErrorFormulario is AvisoSeleccionaCliente or AvisoSeleccionaEmpresa)
            _mensajeErrorFormulario = null;
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        return Task.CompletedTask;
    }

    /// <summary>"Cambiar Cliente empresarial/empresa" en un Centro llegado ya fijado por una cadena (Fase A2) — vuelve a los selectores editables con el catálogo completo.</summary>
    private async Task DesvincularPadresFijadosAsync()
    {
        _padresFijadosPorCadena = false;
        _clienteId = string.Empty;
        _empresaId = string.Empty;
        await CargarEmpresasDisponiblesAsync(null);
    }

    private void AbrirCrearClienteInline(string texto)
    {
        _nombreParaCrearCliente = texto;
        _formularioRapidoClienteVisible = true;
    }

    /// <summary>Un Cliente recién creado no tiene ninguna Empresa todavía — CambiarClienteCreacionAsync ya deja el selector de Empresa vacío y listo para su propio "+ Crear".</summary>
    private async Task ManejarClienteCreadoAsync(ClienteCreadoDto creado)
    {
        _clientesDisponibles = [.. _clientesDisponibles, new ClienteSelectorDto(creado.Id, creado.RazonSocial)];
        await CambiarClienteCreacionAsync(creado.Id.ToString());
        ToastService.Mostrar("Cliente creado correctamente.", TonoToast.Exito);
    }

    private void AbrirCrearEmpresaInline(string texto)
    {
        _nombreParaCrearEmpresa = texto;
        _formularioRapidoEmpresaVisible = true;
    }

    private Task ManejarEmpresaCreadaAsync(EmpresaCreadaDto creada)
    {
        _empresasDisponibles = [.. _empresasDisponibles, new EmpresaSelectorDto(creada.Id, creada.RazonSocial)];
        _empresaId = creada.Id.ToString();
        LimpiarAvisoDeSeleccionFaltante();
        ToastService.Mostrar("Empresa creada correctamente.", TonoToast.Exito);
        return Task.CompletedTask;
    }

    private Task GuardarAsync() => GuardarAsync(crearOtro: false);

    /// <summary>
    /// "Añadir otro centro" (Fase A2): igual que
    /// <see cref="GuardarAsync()"/>, pero al crear con éxito no cierra el
    /// Drawer — limpia solo los campos propios del Centro y mantiene
    /// Cliente/Empresa fijados, para dar de alta varios centros seguidos sin
    /// repetir la búsqueda.
    /// </summary>
    private Task GuardarYCrearOtroAsync() => GuardarAsync(crearOtro: true);

    private async Task GuardarAsync(bool crearOtro)
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

            if (!Guid.TryParse(_clienteId, out var clienteId))
            {
                _mensajeErrorFormulario = AvisoSeleccionaCliente;
                return;
            }

            if (!Guid.TryParse(_empresaId, out var empresaId))
            {
                _mensajeErrorFormulario = AvisoSeleccionaEmpresa;
                return;
            }

            var resultado = await Mediator.Send(
                new CrearCentroCommand(clienteId, empresaId, _nombre, codigoCentro, direccion, contacto, contratoVigenteHasta));

            if (resultado.EsFallido)
            {
                _mensajeErrorFormulario = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Centro creado correctamente.", TonoToast.Exito);

            // Un Centro nuevo puede traer una Empresa o un Cliente empresarial que las pastillas aún no ofrecen.
            await CargarOpcionesDeFiltroAsync();

            if (crearOtro)
            {
                _nombre = string.Empty;
                _codigoCentro = string.Empty;
                _direccion = string.Empty;
                _contacto = string.Empty;
                _contratoVigenteHasta = string.Empty;
                _erroresCampo = new Dictionary<string, string>();
                // El siguiente centro parte de aquí: Cliente y Empresa mantenidos no son un cambio.
                FijarInstantaneaFormulario();
                await CargarAsync();
                return;
            }

            _drawerVisible = false;
            await CargarAsync();
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

    /// <summary>
    /// Validación inline al salir del campo (Project-Hydra-Negocio/tecnico/docs/archive/design/UX_PATTERNS.md, P1-18 de
    /// Project-Hydra-Negocio/MATURITY_REVIEW.md) — hasta ahora el error de "nombre
    /// obligatorio" solo aparecía tras el viaje de ida y vuelta al servidor
    /// en Guardar. Valida solo <see cref="CrearCentroCommand.Nombre"/> con
    /// el mismo validador que ya corre al guardar — el resto del formulario
    /// puede seguir incompleto sin que este campo lo bloquee.
    /// </summary>
    private async Task ValidarNombreAsync()
    {
        const string campo = nameof(CrearCentroCommand.Nombre);

        var resultado = await ValidadorCrear.ValidateAsync(
            new CrearCentroCommand(Guid.Empty, Guid.Empty, _nombre, null, null, null, null),
            opciones => opciones.IncludeProperties(campo));

        if (resultado.IsValid)
            _erroresCampo.Remove(campo);
        else
            _erroresCampo[campo] = resultado.Errors[0].ErrorMessage;
    }

    private bool TodosSeleccionados =>
        _elementosPagina.Count > 0 && _elementosPagina.All(e => _seleccionados.Contains(e.Id));

    /// <summary>
    /// Apagar el modo limpia la selección (Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.9):
    /// dejar filas marcadas que ya no se ven dejaría la barra de acciones en
    /// lote apuntando a algo invisible.
    /// </summary>
    private void AlternarSeleccionMultiple(bool activa)
    {
        _seleccionMultiple = activa;
        if (!activa)
        {
            _seleccionados.Clear();
            // Sin selección múltiple los grupos vuelven a su estado: la fila enfocada puede quedar oculta.
            DesenfocarSiOculta();
        }
    }

    /// <summary>Todo abierto: los grupos (si se agrupa) y el desplegable de cada fila.</summary>
    private bool TodosExpandidos =>
        _elementosPagina.Count > 0
        && _elementosPagina.All(e => _expandidos.Contains(e.Id) && GrupoAbierto(ClienteEmpresarialDe(e)));

    private void AlternarExpansion(Guid id)
    {
        if (!_expandidos.Add(id))
            _expandidos.Remove(id);
    }

    private void AlternarTodosExpandidos(bool expandir)
    {
        if (expandir)
        {
            foreach (var elemento in _elementosPagina)
            {
                _expandidos.Add(elemento.Id);
                _gruposAbiertos.Add(ClienteEmpresarialDe(elemento));
            }
        }
        else
        {
            _expandidos.Clear();
            _gruposAbiertos.Clear();
            DesenfocarSiOculta();
        }
    }

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
            var idsPedidos = _seleccionados.ToList();
            var resultado = await Mediator.Send(new EliminarCentrosCommand(idsPedidos));
            var dto = resultado.Valor;

            // FS-09: el aviso ofrece «Deshacer» sobre los que sí cayeron.
            IReadOnlyList<Guid> eliminados = dto.IdsEliminados ?? [];

            ToastService.Mostrar(
                dto.Errores.Count == 0
                    ? $"{dto.Eliminados} centro(s) eliminado(s)."
                    : $"{dto.Eliminados} eliminado(s). {dto.Errores.Count} no se pudieron borrar: {string.Join(" ", dto.Errores)}",
                dto.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia,
                eliminados.Count > 0 ? Textos["ToastAccionDeshacer"].Value : null,
                eliminados.Count > 0 ? () => DeshacerEliminarLoteAsync(eliminados) : null);

            // Se retiran solo las fichas de los que cayeron (IdsEliminados); un superviviente
            // conserva la suya y su edición sin guardar. Sin ids en el DTO se retiran todas las
            // pedidas: mejor pasarse de retirar que dejar abierta una ficha muerta.
            if (dto.Eliminados > 0)
                WorkspaceService.RetirarSiEstaAbierto(EntidadWorkspace.Centro, dto.IdsEliminados ?? idsPedidos);

            _seleccionados.Clear();
            _confirmarEliminarLoteVisible = false;
            await CargarAsync();
            await CargarOpcionesDeFiltroAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar los centros seleccionados. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _eliminandoLote = false;
        }
    }

    /// <summary>
    /// FS-09 (auditoría UX de flujos sin salida, 2026-09-24): «Deshacer» del aviso
    /// de una eliminación en lote. Restaura los que el lote sí eliminó; sin esto, la
    /// única salida era pedir a un Administrador del Tenant que los recuperase uno a
    /// uno desde Auditoría.
    /// </summary>
    private bool _restaurandoLote;

    private async Task DeshacerEliminarLoteAsync(IReadOnlyList<Guid> ids)
    {
        if (_restaurandoLote) return;
        _restaurandoLote = true;

        try
        {
            var r = await RestauracionEnLote.RestaurarAsync(ids, id => Mediator.Send(new RestaurarCentroCommand(id)));

            ToastService.Mostrar(
                r.Errores.Count == 0
                    ? Textos["ToastLoteRestaurados", r.Restaurados].Value
                    : Textos["ToastLoteRestauradosConErrores", r.Restaurados, r.Errores.Count, string.Join(" ", r.Errores)].Value,
                r.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);

            if (r.Restaurados > 0)
            {
                await CargarAsync();
                await CargarOpcionesDeFiltroAsync();
            }
        }
        finally
        {
            _restaurandoLote = false;
        }
    }

    private async Task ManejarAtajoAsync(string tecla)
    {
        // «e» no depende de que haya filas a la vista: sin fila enfocada edita la ficha que esté
        // abierta, aunque el filtro haya dejado la lista vacía. La fila enfocada siempre se ve:
        // contraer su grupo o recargar la lista la olvida (DesenfocarSiOculta).
        if (tecla == "e")
        {
            if ((_idEnfocado ?? CentroEnVistaRapida) is { } idEditar)
                await AbrirPanelEnEdicionAsync(idEditar);
            return;
        }

        // j/k recorren las filas que se ven, en el orden en que se pintan (los grupos contraídos no cuentan).
        var filas = FilasVisibles();
        if (filas.Count == 0) return;

        switch (tecla)
        {
            case "j":
                {
                    var indiceActual = _idEnfocado is null ? -1 : filas.FindIndex(e => e.Id == _idEnfocado);
                    _idEnfocado = filas[Math.Min(indiceActual + 1, filas.Count - 1)].Id;
                    break;
                }
            case "k":
                {
                    var indiceActual = _idEnfocado is null ? 0 : filas.FindIndex(e => e.Id == _idEnfocado);
                    _idEnfocado = filas[Math.Max(indiceActual - 1, 0)].Id;
                    break;
                }
            // x y Enter solo sobre una fila que se ve: un foco que quedó en un grupo contraído no cuenta.
            // x activa la selección múltiple (como en la maqueta): con ella los grupos se ven abiertos y la
            // casilla de la fila marcada está a la vista, así que el lote nunca lleva un Centro oculto.
            case "x":
                if (_idEnfocado is { } idAlternar && filas.Any(e => e.Id == idAlternar))
                {
                    _seleccionMultiple = true;
                    AlternarSeleccion(idAlternar, !_seleccionados.Contains(idAlternar));
                }
                break;
            case "Enter":
                if (_idEnfocado is { } idAbrir && filas.Any(e => e.Id == idAbrir))
                {
                    // Enter abre la vista previa (el panel del centro), como el nombre de la fila.
                    await AbrirPanelAsync(idAbrir);
                }
                break;
        }

        StateHasChanged();
    }
    /// <summary>
    /// Orden de la lista. Por defecto null: cliente y luego nombre, el orden de
    /// catalogo. El orden por cumplimiento se pide expresamente (blueprint
    /// seccion 3.1, DDL-036) y existe para atacar los peores centros sin
    /// depender de que haya una visita proxima.
    /// </summary>
    private string? _ordenarPor;
    private bool _ordenDescendente;

    private bool OrdenPorCumplimiento => _ordenarPor == nameof(CentroListaDto.CumplimientoPorcentaje);

    /// <summary>
    /// Segunda línea de la identidad: el código, y además el Cliente empresarial cuando no se agrupa (agrupado,
    /// ya lo dice la cabecera del grupo). Vacía si no hay nada que decir.
    /// </summary>
    private string MetaCentro(CentroListaDto centro)
    {
        var partes = new List<string>(2);
        if (!_agruparPorCliente)
            partes.Add(centro.ClienteRazonSocial);
        if (!string.IsNullOrEmpty(centro.CodigoCentro))
            partes.Add(centro.CodigoCentro);
        return string.Join(" · ", partes);
    }

    private async Task CambiarOrdenAsync(string? ordenarPor)
    {
        // Volver a pedir el mismo orden invierte el sentido: es el gesto que
        // el usuario ya conoce de cualquier cabecera de tabla.
        if (string.Equals(_ordenarPor, ordenarPor, StringComparison.Ordinal))
        {
            _ordenDescendente = !_ordenDescendente;
        }
        else
        {
            _ordenarPor = ordenarPor;
            _ordenDescendente = false;
        }

        // El orden viaja en la URL como los filtros: recargar o compartir el enlace lo conserva.
        NavigationManager.ActualizarFiltroEnUrl("orden", _ordenarPor is null
            ? null
            : _ordenDescendente ? OrdenCumplimientoDescendenteEnUrl : OrdenCumplimientoEnUrl);

        _pagina = 1;
        await CargarAsync();
    }

    /// <summary>
    /// Incidencias del ambito Empresa de un centro. Se derivan de los recuentos
    /// que la fila ya tiene: el bloque Empresa del acordeon no lanza ninguna
    /// consulta propia (OD-13, blueprint seccion 3.3).
    /// </summary>
    private static IReadOnlyList<IncidenciaCentroDto> IncidenciasDeEmpresa(CentroListaDto centro) =>
        centro.Recuentos.Vencidas.Concat(centro.Recuentos.Proximas)
            .Where(i => i.Ambito == AmbitoCausa.Empresa)
            .ToList();

    /// <summary>
    /// Motivo bajo la pastilla de estado del Centro: cuántos documentos hay en
    /// «vencidas» y cuántos en «próximas» (el reparto de
    /// <c>ObtenerCentrosQuery.Desglosar</c>). Es lo que decían las columnas
    /// «Venc.» y «Próx.», retiradas el 2026-10-09; cada parte abre su ventana de
    /// contexto con las incidencias.
    /// </summary>
    private string MotivoVencidos(int cantidad) =>
        cantidad == 1 ? Textos["MotivoUnVencido"].Value : Textos["MotivoVencidos", cantidad].Value;

    private string MotivoProximos(int cantidad) =>
        cantidad == 1 ? Textos["MotivoUnProximo"].Value : Textos["MotivoProximos", cantidad].Value;

    /// <summary>
    /// Nombre accesible del disparador del motivo. Empieza por el texto que se ve («2 vencidos») para que
    /// quien lo nombre de viva voz lo active (WCAG 2.5.3, el nombre contiene la etiqueta visible), y sigue
    /// con la frase completa y el reparto por ámbito de <see cref="DescribirRecuento"/>.
    /// </summary>
    private static string EtiquetaDeMotivo(string textoVisible, IReadOnlyList<IncidenciaCentroDto> incidencias, string calificativo) =>
        $"{textoVisible}. {DescribirRecuento(incidencias, calificativo)}";

    /// <summary>
    /// Nombre accesible de un badge de solo recuento. Es lo unico que oye un
    /// lector de pantalla, asi que dice el total Y el reparto por ambito: un
    /// numero desnudo deja el color como unico portador de significado, que es
    /// lo que 02 seccion 8 prohibe (DDL-033).
    /// </summary>
    private static string DescribirRecuento(IReadOnlyList<IncidenciaCentroDto> incidencias, string calificativo)
    {
        var deEmpresa = incidencias.Count(i => i.Ambito == AmbitoCausa.Empresa);
        var deTrabajadores = incidencias.Count - deEmpresa;
        var cabeza = incidencias.Count == 1
            ? $"1 documento {calificativo}"
            : $"{incidencias.Count} documentos {(calificativo.EndsWith('o') ? calificativo + "s" : calificativo)}";

        var partes = new List<string>();
        if (deEmpresa > 0) partes.Add(deEmpresa == 1 ? "1 de empresa" : $"{deEmpresa} de empresa");
        if (deTrabajadores > 0) partes.Add(deTrabajadores == 1 ? "1 de trabajadores" : $"{deTrabajadores} de trabajadores");

        return partes.Count == 0 ? cabeza : $"{cabeza}: {string.Join(" y ", partes)}";
    }

    /// <summary>
    /// Detalle visible de la ventana, agrupado por ambito. La agrupacion no es
    /// estetica: el blueprint seccion 3.2 exige que el desglose declare de
    /// quien es cada incidencia, porque el recuento agrega los dos ambitos
    /// (DDL-031, DDL-047).
    /// </summary>
    private RenderFragment DesgloseIncidencias(CentroListaDto centro, IReadOnlyList<IncidenciaCentroDto> incidencias) => builder =>
    {
        // Numeros de secuencia LITERALES, no una variable incrementada
        // (ASP0006): el analizador de Blazor lo senala porque el numero debe
        // ser constante para la misma posicion del codigo fuente, no
        // depender de cuantas iteraciones se ejecutaron antes. Reutilizar el
        // mismo literal en cada vuelta del foreach es el patron que la
        // documentacion de Blazor recomienda para bucles de longitud
        // variable — el diffing usa la posicion en el arbol, no un contador
        // unico por elemento.
        foreach (var grupo in new[] { AmbitoCausa.Empresa, AmbitoCausa.Trabajador })
        {
            var deEsteAmbito = incidencias.Where(i => i.Ambito == grupo).ToList();
            if (deEsteAmbito.Count == 0) continue;

            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "class", "ventana-linea ventana-grupo");
            builder.AddContent(2, grupo == AmbitoCausa.Empresa ? "Empresa" : "Trabajadores");
            builder.CloseElement();

            foreach (var incidencia in deEsteAmbito)
            {
                // Pulsable solo si hay algo que abrir y el rol puede guardarlo: una incidencia sin
                // Documento ni Tipo, o vista por quien solo consulta, sigue siendo una línea de texto.
                if (EsCorregible(centro, incidencia))
                {
                    var descripcion = incidencia.Descripcion;
                    builder.OpenComponent<VentanaContextoElemento>(6);
                    builder.AddComponentParameter(7, nameof(VentanaContextoElemento.Ayuda), Comunes["VentanaIncidenciaAyuda"].Value);
                    builder.AddComponentParameter(8, nameof(VentanaContextoElemento.AlPulsar),
                        EventCallback.Factory.Create(this, () => CorregirIncidenciaAsync(centro, incidencia)));
                    builder.AddComponentParameter(9, nameof(VentanaContextoElemento.ChildContent),
                        (RenderFragment)(contenido => contenido.AddContent(10, descripcion)));
                    builder.CloseComponent();
                    continue;
                }

                builder.OpenElement(3, "span");
                builder.AddAttribute(4, "class", "ventana-linea");
                builder.AddContent(5, incidencia.Descripcion);
                builder.CloseElement();
            }
        }
    };

    /// <summary>
    /// Los criterios de «Exportar esta vista»: los mismos que esta página pasa a la consulta del
    /// listado, con los nombres de parámetro del endpoint de exportación, que los lee igual.
    /// </summary>
    private Dictionary<string, string?> CriteriosExportar => new()
    {
        ["q"] = _busqueda,
        ["estado"] = _estadoFiltro,
        ["cliente"] = _clienteFiltro,
        ["empresa"] = _empresaFiltro,
        ["centro"] = _centroIdFiltro?.ToString(),
        ["orden"] = _ordenarPor,
        ["desc"] = _ordenDescendente ? "true" : null,
    };
}
