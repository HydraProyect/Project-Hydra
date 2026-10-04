using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Commands.EliminarCentros;
using CaeManager.Application.Centros.Commands.RestaurarCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
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
    private int _tamanoPagina = 20;

    private string _busqueda = string.Empty;
    private string _estadoFiltro = string.Empty;
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
    // D-16: baja de un solo Centro desde el «⋯» de su fila. Reutiliza el diálogo y el camino de la
    // baja en lote (EliminarCentrosCommand con un id, «Deshacer» incluido) sin tocar la selección.
    private Guid? _centroAEliminarId;
    private string _centroAEliminarNombre = string.Empty;

    private void PedirEliminarCentro(CentroListaDto centro)
    {
        _centroAEliminarId = centro.Id;
        _centroAEliminarNombre = centro.Nombre;
        _confirmarEliminarLoteVisible = true;
    }

    private void CambiarVisibilidadConfirmarEliminar(bool visible)
    {
        _confirmarEliminarLoteVisible = visible;
        if (!visible)
            _centroAEliminarId = null;
    }
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

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ITenantActual TenantActual { get; set; } = default!;

    /// <summary>
    /// La empresa gestionada activa, solo para quien alcanza varias (mismo criterio que el selector
    /// de la barra lateral): la cabecera dice de cuál es la lista. Mismo patrón que Trabajadores.
    /// </summary>
    private ClienteAutorizadoDto? _empresaActiva;

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
        // Hasta resolver la empresa activa no se monta la lista ni sus acciones: con la consulta en
        // vuelo el render saldría con «hay empresa» y lanzaría la carga (y la exportación) del origen.
        var token = _ciclo.Token;
        try
        {
            var contexto = await ContextoEmpresaActiva.ResolverAsync(Mediator, TenantActual, token);
            if (_desechado)
                return;

            _empresaActiva = contexto.Activa;
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
        _estadoFiltro = Enum.TryParse<EstadoCentro>(EstadoInicial, out _) ? EstadoInicial! : string.Empty;
        _centroIdFiltro = CentroId;
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
        var estadoDeLaUrl = Enum.TryParse<EstadoCentro>(EstadoInicial, out _) ? EstadoInicial! : string.Empty;

        if (deLaUrl == _busqueda && estadoDeLaUrl == _estadoFiltro && CentroId == _centroIdFiltro)
            return;

        _busqueda = deLaUrl;
        _estadoFiltro = estadoDeLaUrl;
        _centroIdFiltro = CentroId;
        await CargarAsync(resetPagina: true);
    }

    private async Task CargarAsync(bool resetPagina = false)
    {
        if (resetPagina)
            _pagina = 1;

        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ObtenerCentrosQuery(
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                ClienteId: null,
                Estado: Enum.TryParse<EstadoCentro>(_estadoFiltro, out var estado) ? estado : null,
                OrdenarPor: _ordenarPor,
                Descendente: _ordenDescendente,
                Pagina: _pagina,
                TamanoPagina: _tamanoPagina,
                CentroId: _centroIdFiltro));

            _totalElementos = resultado.TotalElementos;
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
        if (actualizado is null) return;

        var indice = _elementosPagina.FindIndex(c => c.Id == centroId);
        if (indice >= 0)
            _elementosPagina[indice] = actualizado;

        StateHasChanged();
    }

    private DrawerAsignacionMasiva _drawerAsignacion = default!;

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

    private async Task CambiarEstadoAsync(string valor)
    {
        _estadoFiltro = valor;
        NavigationManager.ActualizarFiltroEnUrl("estado", valor);
        await CargarAsync(resetPagina: true);
    }

    private bool HayFiltrosActivos =>
        !string.IsNullOrWhiteSpace(_busqueda) || !string.IsNullOrWhiteSpace(_estadoFiltro);

    /// <summary>
    /// «Limpiar todo»: quita los dos filtros <b>también de la URL</b>, en una sola llamada (cada
    /// <c>NavigateTo</c> lee la URL vigente y varias seguidas se pisan; ver el helper). Si la URL
    /// conservara alguno, <c>OnParametersSetAsync</c> —que re-sincroniza desde ella— lo devolvería.
    /// </summary>
    private async Task LimpiarFiltrosAsync()
    {
        _busqueda = string.Empty;
        _estadoFiltro = string.Empty;
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["q"] = null,
            ["estado"] = null,
        });
        await CargarAsync(resetPagina: true);
    }

    // --- Patrón único de lista (Project-Hydra-Negocio/tecnico/CONTRATO-PATRON-PANTALLA-LISTA-2026-09-28.md) ---

    /// <summary>
    /// Nombre de la fila, «Vista previa» del «⋯» y Enter sobre la fila enfocada: la vista previa (pieza 6). En
    /// Centros es el panel del Context Workspace, que ya existe; el contrato prohíbe sumarle un drawer.
    /// </summary>
    private Task AbrirPanelAsync(Guid id)
    {
        var centro = _elementosPagina.FirstOrDefault(e => e.Id == id);
        return centro is null
            ? Task.CompletedTask
            : WorkspaceService.AbrirAsync(EntidadWorkspace.Centro, centro.Id, centro.Nombre, "informacion");
    }

    /// <summary>«Abrir ficha 360» del «⋯»: la página /centros/{id}.</summary>
    private void AbrirFichaCentro(Guid id) => NavigationManager.NavigateTo($"/centros/{id}");

    private string EtiquetaFiltroBusqueda => Textos["ChipBusqueda", _busqueda].Value;

    private string EtiquetaFiltroEstado =>
        Textos["ChipEstado", EstadoCentroUi.Opciones.FirstOrDefault(o => o.Valor == _estadoFiltro)?.Texto ?? "—"].Value;

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

    private const string AvisoSeleccionaCliente = "Selecciona un Cliente empresarial.";
    private const string AvisoSeleccionaEmpresa = "Selecciona una empresa.";

    private void AlElegirEmpresa(string valor)
    {
        _empresaId = valor;
        LimpiarAvisoDeSeleccionFaltante();
    }

    /// <summary>
    /// El aviso «Selecciona un Cliente empresarial.» (o «Selecciona una empresa.») es de un intento anterior de guardar: al rellenar el
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
        ToastService.Mostrar("Cliente empresarial creado correctamente.", TonoToast.Exito);
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
            _seleccionados.Clear();
    }

    private bool TodosExpandidos =>
        _elementosPagina.Count > 0 && _elementosPagina.All(e => _expandidos.Contains(e.Id));

    private void AlternarExpansion(Guid id)
    {
        if (!_expandidos.Add(id))
            _expandidos.Remove(id);
    }

    private void AlternarTodosExpandidos(bool expandir)
    {
        if (expandir)
            foreach (var elemento in _elementosPagina) _expandidos.Add(elemento.Id);
        else
            _expandidos.Clear();
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
            var individual = _centroAEliminarId;
            var idsPedidos = individual is { } uno ? [uno] : _seleccionados.ToList();
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

            if (individual is null)
                _seleccionados.Clear();
            else
                _seleccionados.Remove(individual.Value);
            _centroAEliminarId = null;
            _confirmarEliminarLoteVisible = false;
            await CargarAsync();
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
                await CargarAsync();
        }
        finally
        {
            _restaurandoLote = false;
        }
    }

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
    /// 3ª ranura de indicadores del Centro (contrato de fidelidad 2026-08-09
    /// § 1.6, bug C-2), cuando no hay visita que mostrar en su lugar. El
    /// Badge de Estado solo aporta algo que los recuentos de vencidos/
    /// próximos no digan ya: Vencido/Próximo/Falta documentación quedan
    /// reflejados en esas dos ranuras (mismo criterio de bucketing que
    /// <c>ObtenerCentrosQuery.Desglosar</c>), así que repetirlos aquí era
    /// justo la redundancia que hacía desbordar la columna. Urgente sí necesita la ranura: desde
    /// que <c>Desglosar</c> lo funde con Vencido/Faltante en "vencidas"
    /// (mismo tono Peligro que ya le da <c>EstadoDocumentoUi.Tono</c>), el
    /// recuento por sí solo ya no distingue Urgente de un vencimiento
    /// consumado — el Badge de Estado es la única señal que sí lo hace.
    /// "Sin incidencias" (0 y 0) también, para que la fila no quede
    /// completamente muda.
    /// </summary>
    private static bool MostrarEstadoEnIndicadores(CentroListaDto centro) =>
        centro.Estado is EstadoCentro.Urgente
        || (centro.Recuentos.TotalVencidas == 0 && centro.Recuentos.TotalProximas == 0);

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
    private static RenderFragment DesgloseIncidencias(IReadOnlyList<IncidenciaCentroDto> incidencias) => builder =>
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
                builder.OpenElement(3, "span");
                builder.AddAttribute(4, "class", "ventana-linea");
                builder.AddContent(5, incidencia.Descripcion);
                builder.CloseElement();
            }
        }
    };
}
