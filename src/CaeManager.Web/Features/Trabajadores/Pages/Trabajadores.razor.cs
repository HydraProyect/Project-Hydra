using CaeManager.Application.Trabajadores.Queries.ObtenerEmpleadoresDeTrabajadoresVisibles;
using System.Text.Json;
using CaeManager.Application.Alertas;
using CaeManager.Application.Common;
using CaeManager.Application.Asignaciones.Commands.CrearAsignaciones;
using CaeManager.Application.Asignaciones.Queries.ObtenerDocumentosFaltantesParaAsignacion;
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Application.Trabajadores.Commands.EliminarTrabajadores;
using CaeManager.Application.Trabajadores.Commands.RestaurarTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Application.Tenants.Queries.UsaRotulosPrimeraPersona;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Components.Workspace;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.QuickGrid;
using CaeManager.Web.Features.Trabajadores.Recursos;
using CaeManager.Web.Recursos;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Trabajadores.Pages;

public partial class Trabajadores : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    private Modal? _modalAsignarCentro;
    /// <summary>
    /// Se cancela al salir de la página: la resolución de la empresa activa que siga en vuelo deja de trabajar
    /// para nadie y su respuesta tardía no repinta un componente ya retirado.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    // ── La fila se refresca tras guardar en la vista rápida ─────────────────────────────────────
    // El panel vive en MainLayout y guarda sin pasar por esta página: avisa por
    // ContextWorkspaceService.OnEntidadGuardada. Se vuelve a pedir SOLO esa fila y se sustituye
    // en sitio (mismo criterio que Centros.RefrescarCentroAsync): filtros, orden, página,
    // selección, fila enfocada y desplazamiento no se tocan, y la fila permanece aunque el
    // cambio la saque del filtro activo, hasta la siguiente carga. Los recuentos de la franja
    // tampoco se recalculan hasta entonces.

    /// <summary>
    /// La siguiente petición de QuickGrid se sirve de <see cref="_elementosPagina"/> sin consultar:
    /// QuickGrid solo repinta sus filas cuando su proveedor le entrega una página, y una carga
    /// de verdad limpiaría la selección y la fila enfocada. El total no cambia, así que no hay
    /// segunda petición (ver RecargarAsync).
    /// </summary>
    private bool _servirPaginaEnMemoria;

    private void AlGuardarEntidad(EntidadWorkspace tipo, Guid id)
    {
        if (tipo == EntidadWorkspace.Trabajador)
            _ = InvokeAsync(() => RefrescarFilaAsync(id));
    }

    private async Task RefrescarFilaAsync(Guid id)
    {
        // Con una carga en vuelo no se sustituye nada: la sustitución caería sobre una página que
        // está a punto de cambiar. Hueco conocido: si esa carga leyó antes de que el guardado
        // fuera firme, la fila conserva el dato anterior hasta la siguiente carga.
        if (_desechado || _grid is null || _cargando || !_elementosPagina.Any(e => e.Id == id))
            return;

        var carga = _cargaVigente;
        try
        {
            // Misma pregunta de estado que la carga de página (ConRecuentosPorEstado): sin ella el
            // handler toma el camino que deja sin estado a quien no tiene documentos, y la fila
            // pasaría de «Sin incidencias» a «Sin documentos» al refrescarla. Y el desglose se pide
            // igual que allí: sin él la fila refrescada perdería el motivo y «Registrados vigentes».
            var resultado = await Mediator.Send(
                new ObtenerTrabajadoresQuery(
                    Busqueda: null, ConDesgloseDocumental: true, ConRecuentosPorEstado: true, TrabajadorId: id),
                _ciclo.Token);
            var indice = _elementosPagina.FindIndex(e => e.Id == id);
            if (_desechado || _grid is null || _cargando || carga != _cargaVigente || indice < 0
                || resultado.Elementos.FirstOrDefault() is not { } actualizada)
                return;

            _elementosPagina[indice] = actualizada;
            _servirPaginaEnMemoria = true;
            await _grid.RefreshDataAsync();
        }
        catch (Exception)
        {
            // El guardado ya es firme: que falle la relectura no es un error que enseñar. La
            // fila conserva el dato anterior hasta la siguiente carga, como antes de este aviso.
        }
        finally
        {
            _servirPaginaEnMemoria = false;
        }
    }

    public void Dispose()
    {
        if (_desechado)
            return;

        _desechado = true;
        WorkspaceService.OnEntidadGuardada -= AlGuardarEntidad;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    /// <summary>Quien mira no alcanza nada en este Tenant (<see cref="CaeManager.Web.Features.IncorporacionCartera.Components.VacioSegunAlcance"/>):
    /// sin «+ Nuevo» en cabecera, para no duplicar lo que quizá ya existe fuera de su cartera.</summary>
    private bool _alcanceCero;

    /// <summary>Tamaño de página por defecto y mínimo del selector: por debajo de él no hay paginador.</summary>
    private const int TamanoPaginaMinimo = 20;

    private readonly PaginationState _paginacion = new() { ItemsPerPage = TamanoPaginaMinimo };

    // H2 (Project-Hydra-Negocio/tecnico/docs/ux-audit/02-clientes.md): paginador único en español, ver Clientes.razor.cs.
    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_totalElementos / (double)_paginacion.ItemsPerPage));

    private Task CambiarPaginaAsync(int pagina) => _paginacion.SetCurrentPageIndexAsync(pagina - 1);

    // H5 (Project-Hydra-Negocio/tecnico/docs/ux-audit/05-trabajadores-vehiculos.md): selector de tamaño de página, compartido por PaginadorSimple.razor.
    // Una sola petición: SetCurrentPageIndexAsync ya avisa a QuickGrid aunque la
    // página no cambie, así que refrescar además la rejilla pedía lo mismo dos
    // veces (ver RecargarAsync).
    private Task CambiarTamanoPaginaAsync(int tamano)
    {
        _paginacion.ItemsPerPage = tamano;
        return _paginacion.SetCurrentPageIndexAsync(0);
    }

    private QuickGrid<TrabajadorListaDto>? _grid;

    private string _busqueda = string.Empty;
    private string _estadoFiltro = string.Empty;
    private string _filtroEmpresaId = string.Empty;
    private string _filtroSubcontrataId = string.Empty;

    /// <summary>Centro elegido en la pastilla «Centro» (<c>?centro=</c>): Trabajadores con una Asignación activa en él.</summary>
    private string _filtroCentroId = string.Empty;

    /// <summary>
    /// Opciones de la pastilla «Centro»: los Centros que el usuario ya puede ver (mismo alcance que comprueba la
    /// consulta del listado). Si fallan, la lista se pinta igual y la pastilla solo ofrece «Todos».
    /// </summary>
    private IReadOnlyList<CentroSelectorDto> _centrosFiltro = [];

    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    /// <summary>Filas por estado para la franja, sin el filtro de estado aplicado. <c>null</c> hasta la primera carga.</summary>
    private IReadOnlyDictionary<string, int>? _recuentosPorEstado;

    private IReadOnlyList<EmpresaSelectorDto> _empresasDisponibles = [];
    private IReadOnlyList<SubcontrataSelectorDto> _subcontratasDisponibles = [];

    /// <summary>
    /// Opciones de la pastilla «Empresa»: los empleadores de los Trabajadores visibles, por el mismo alcance que la
    /// lista. Distintas de <see cref="_empresasDisponibles"/>/<see cref="_subcontratasDisponibles"/>, que son las del
    /// alta (alcance de gestión y catálogo de Subcontratas) y no deben enseñarse como filtro a quien no gestiona.
    /// </summary>
    private EmpleadoresDeTrabajadoresDto _empleadoresFiltro = new([], []);
    private long _versionEmpleadores;

    // DDL-072 (misma fuente que el enlace «trabajadores» de CatalogoMenuLateral y _tituloPagina de
    // Empresas.razor.cs: UsaRotulosPrimeraPersonaQuery): "Mis trabajadores" solo si perfil Cliente
    // Directo y el usuario es del Tenant propietario; "Trabajadores" en cualquier otro caso.
    private string? _tituloPagina;

    private string TituloPagina => _tituloPagina ?? Textos["TituloPagina"];

    private bool _drawerVisible;
    private string _tipoEmpleador = "empresa";

    // DDL-076: en perfil Cliente Directo con una única Empresa, el selector
    // de Empresa no aparece — se resuelve en silencio. Reaparece si el
    // tenant tiene más de una razón social (excepción por dato, no por
    // configuración) o si el perfil es Consultora.
    private bool _resolverEmpresaEnSilencio;

    private string _empresaId = string.Empty;
    private string _subcontrataId = string.Empty;
    private string _dni = string.Empty;
    private string _nombre = string.Empty;
    private string _apellidos = string.Empty;
    private string _fechaNacimiento = string.Empty;
    private string _email = string.Empty;
    private string _telefono = string.Empty;
    private string _observaciones = string.Empty;
    private string _alias = string.Empty;
    private string _puesto = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    // Un clic en la fila, su nombre o Enter sobre la fila enfocada abren la vista rápida: el
    // panel de 520 px del Context Workspace (mismo patrón que Empresas). A la página
    // Trabajador 360 (/trabajadores/{id}) se va con el icono 360 de la fila o con el del panel.
    private Task AbrirVistaRapidaAsync(Guid id) =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Trabajador, id, NombreDe(id), "informacion");

    /// <summary>
    /// Tecla «e»: la vista rápida de la fila enfocada, ya en edición (el lápiz de la cabecera
    /// del panel). Si el rol no puede escribir, el panel se abre y se queda en lectura.
    /// </summary>
    private Task AbrirVistaRapidaEnEdicionAsync(Guid id) =>
        WorkspaceService.AbrirEnEdicionAsync(EntidadWorkspace.Trabajador, id, NombreDe(id));

    // El Trabajador del panel puede no estar en la página (el filtro lo dejó fuera): su nombre
    // es entonces el del frame abierto.
    private string NombreDe(Guid id) =>
        _elementosPagina.FirstOrDefault(t => t.Id == id) is { } trabajador
            ? NombreCompleto(trabajador)
            : WorkspaceService.FrameActual is { } frame && frame.EntidadId == id ? frame.TituloVisible : string.Empty;

    /// <summary>Trabajador cuyo panel está abierto arriba de la pila del Context Workspace, si lo hay.</summary>
    private Guid? TrabajadorEnVistaPrevia =>
        WorkspaceService.FrameActual is { Tipo: EntidadWorkspace.Trabajador } frame ? frame.EntidadId : null;

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    /// <summary>
    /// Filtro de estado documental (ver ICalculoEstadoDocumentalService) — esta
    /// entidad no tiene estado propio en el modelo, se deriva de sus Documentos.
    /// </summary>
    [SupplyParameterFromQuery(Name = "estado")]
    public string? EstadoInicial { get; set; }

    /// <summary>
    /// Empleador elegido en el filtro: una Empresa (<c>?empresa=</c>) o una
    /// subcontrata (<c>?subcontrata=</c>), nunca las dos. Con ambas en la URL
    /// gana la subcontrata, como en <see cref="ValorFiltroEmpleador"/>.
    /// </summary>
    [SupplyParameterFromQuery(Name = "empresa")]
    public string? EmpresaInicial { get; set; }

    [SupplyParameterFromQuery(Name = "subcontrata")]
    public string? SubcontrataInicial { get; set; }

    /// <summary>Centro elegido en el filtro (<c>?centro=</c>). Se compone con el empleador: no se excluyen.</summary>
    [SupplyParameterFromQuery(Name = "centro")]
    public string? CentroInicial { get; set; }

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosTrabajadores> Textos { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosComunes> Comunes { get; set; } = default!;
    [Inject] private IValidator<CrearTrabajadorCommand> ValidadorCrear { get; set; } = default!;

    /// <summary>Comando del palette "Crear trabajador" / "Crear trabajador «nombre»" (P3-31): abre el Drawer, con el nombre precargado si viene del palette.</summary>
    [SupplyParameterFromQuery] public string? Accion { get; set; }
    [SupplyParameterFromQuery] public string? Nombre { get; set; }

    private GridItemsProvider<TrabajadorListaDto>? _proveedorElementos;

    // --- P3-31: selección múltiple, atajos j/k, filtros guardados ---
    private readonly HashSet<Guid> _seleccionados = [];

    /// <summary>
    /// Los checkboxes de fila solo se pintan con esto activo (Centro 360,
    /// Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.9) — son ruido permanente para una acción
    /// ocasional. Apagarlo limpia la selección: dejar filas marcadas que ya
    /// no se ven dejaría la barra de acciones en lote apuntando a algo
    /// invisible.
    /// </summary>
    private bool _seleccionMultiple;

    private void AlternarSeleccionMultiple(bool activa)
    {
        _seleccionMultiple = activa;
        if (!activa)
            _seleccionados.Clear();
    }
    private List<TrabajadorListaDto> _elementosPagina = [];
    private Guid? _idEnfocado;
    private bool _eliminandoLote;
    private bool _confirmarEliminarLoteVisible;

    private IReadOnlyList<FiltroGuardadoDto> _filtrosGuardados = [];
    private FiltroGuardadoDto? _filtroGuardadoAEliminar;
    private bool _eliminandoFiltroGuardado;

    private void CambiarVisibilidadBorradoFiltroGuardado(bool visible)
    {
        if (!visible) _filtroGuardadoAEliminar = null;
    }
    private bool _mostrarGuardarFiltro;
    private string _nombreFiltroNuevo = string.Empty;
    private bool _guardandoFiltro;

    /// <summary>Error del servidor al guardar el filtro: se ve en el aviso fijo del ModalFormulario (D-20), no en un toast que desaparece.</summary>
    private string? _mensajeErrorFiltro;

    // --- Fase B: "Asignar a centro…" en lote desde /trabajadores ---
    private bool _asignarCentroVisible;
    private IReadOnlyList<CentroSelectorDto> _centrosDisponiblesParaAsignar = [];
    private string _centroIdParaAsignar = string.Empty;
    private string _fechaAltaParaAsignar = string.Empty;
    private IReadOnlyList<DocumentoFaltanteDto> _documentosFaltantesParaAsignar = [];
    private bool _asignandoLote;

    private IReadOnlyList<OpcionBuscable> OpcionesCentrosParaAsignar => _centrosDisponiblesParaAsignar
        .Select(c => new OpcionBuscable(c.Id.ToString(), $"{c.Nombre} ({c.ClienteRazonSocial})"))
        .ToList();

    /// <summary>
    /// Lo que guarda un filtro de esta pantalla. <c>Estado</c> (la franja de estado) y <c>CentroId</c> (la pastilla
    /// «Centro») llegaron después: son opcionales para que los filtros ya guardados sin ellos se sigan leyendo, y
    /// entonces se aplican sin estado y sin Centro.
    /// </summary>
    private record FiltrosTrabajadoresJson(
        string? Busqueda, string? EmpresaId, string? SubcontrataId, string? Estado = null, string? CentroId = null);

    /// <summary>Estado 4a del mockup del selector: hay que elegir una empresa de la cartera antes de ver la lista.</summary>
    private bool _sinEmpresaSeleccionada;

    /// <summary>La empresa activa aún no se ha resuelto: se pinta una carga en vez de la lista.</summary>
    private bool _resolviendoEmpresa = true;

    [Inject] private ITenantActual TenantActual { get; set; } = default!;

    [CascadingParameter] private Task<AuthenticationState>? EstadoAutenticacion { get; set; }

    /// <summary>
    /// El rol efectivo puede escribir (mismos roles que <c>SoloConEscritura</c>). Sin él no se ofrece
    /// la selección múltiple: solo alimenta la barra de lote (eliminar, asignar a centro), toda ella
    /// escritura, y las casillas no servirían para nada.
    /// </summary>
    private bool _puedeEscribir;

    protected override async Task OnInitializedAsync()
    {
        WorkspaceService.OnEntidadGuardada += AlGuardarEntidad;
        // Delegado estable — ver Clientes.razor.cs (bucle de recargas de QuickGrid).
        _proveedorElementos = ProveerElementosAsync;

        if (EstadoAutenticacion is not null)
        {
            var usuario = (await EstadoAutenticacion).User;
            _puedeEscribir = Roles.ConEscrituraCsv.Split(',').Any(usuario.IsInRole);
        }

        // Hasta resolver la empresa activa no se monta la lista ni sus acciones: con la consulta en
        // vuelo el render saldría con «hay empresa» y lanzaría la carga (y la exportación) del origen.
        try
        {
            var contexto = await ContextoEmpresaActiva.ResolverAsync(Mediator, TenantActual, _ciclo.Token);
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

        // Opciones de la pastilla «Empresa». Si fallan, la lista se pinta igual y la pastilla solo ofrece «Todas».
        await CargarEmpleadoresFiltroAsync();
        if (_desechado)
            return;

        await CargarCentrosFiltroAsync();
        if (_desechado)
            return;

        var primeraPersona = await Mediator.Send(new UsaRotulosPrimeraPersonaQuery());
        _tituloPagina = primeraPersona
            ? Textos["TituloPaginaClienteDirecto"].Value
            : Textos["TituloPagina"].Value;

        if (Accion == "crear")
        {
            await AbrirCrearAsync();
            if (!string.IsNullOrWhiteSpace(Nombre))
                _nombre = Nombre;
            // Lo que trae la URL no es un cambio de quien edita.
            FijarInstantaneaFormulario();
        }

        _filtrosGuardados = await Mediator.Send(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Trabajadores));
    }

    /// <summary>
    /// Se re-ejecuta en cada navegación dentro de la propia página (recargar,
    /// compartir la URL, volver atrás) — no solo en el primer render — para
    /// que el filtro de la URL sea la fuente de verdad, no solo su semilla
    /// inicial (P1-18 de Project-Hydra-Negocio/MATURITY_REVIEW.md).
    ///
    /// <para>
    /// Si la URL trae un filtro distinto del que hay en pantalla y la rejilla
    /// ya existe, se recarga: antes solo se copiaba el valor, y volver atrás a
    /// <c>?q=Salas</c> dejaba el chip diciendo «Salas» sobre las filas de la
    /// búsqueda anterior. En el primer paso la rejilla todavía no existe y su
    /// primera carga ya lee los valores de aquí. Los cambios que hace la propia
    /// página (buscar, quitar un chip) escriben primero el campo y después la
    /// URL, así que al llegar aquí ya coinciden y no duplican la consulta.
    /// </para>
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        // Retirada la página con la resolución en vuelo, ComponentBase aún invoca esto: no se procesan
        // parámetros ni acciones de URL de un componente que ya no existe, ni en el estado 4a (una
        // ?accion=guardar-filtro no abre el diálogo contra el Tenant de origen).
        if (_desechado || _resolviendoEmpresa || _sinEmpresaSeleccionada)
            return;

        var deLaUrl = TerminoBusquedaInicial ?? string.Empty;
        var estadoDeLaUrl = EstadoDocumentoUi.SeleccionDocumentalValida(EstadoInicial);

        var subcontrataDeLaUrl = IdDesdeUrl(SubcontrataInicial);
        var empresaDeLaUrl = subcontrataDeLaUrl.Length > 0 ? string.Empty : IdDesdeUrl(EmpresaInicial);

        var centroDeLaUrl = IdDesdeUrl(CentroInicial);

        var cambiaronLosFiltros = deLaUrl != _busqueda || estadoDeLaUrl != _estadoFiltro
            || empresaDeLaUrl != _filtroEmpresaId || subcontrataDeLaUrl != _filtroSubcontrataId
            || centroDeLaUrl != _filtroCentroId;
        _busqueda = deLaUrl;
        _estadoFiltro = estadoDeLaUrl;
        _filtroEmpresaId = empresaDeLaUrl;
        _filtroSubcontrataId = subcontrataDeLaUrl;
        _filtroCentroId = centroDeLaUrl;

        if (cambiaronLosFiltros && _grid is not null)
            await RecargarAsync();

        // A diferencia de "accion=crear" (OnInitializedAsync, solo se
        // ejecuta al montar: siempre llega desde otra página), "guardar-filtro"
        // tiene que funcionar estando YA en /trabajadores — el propio Command
        // Palette navega a la misma ruta añadiendo el query string, sin
        // recrear el componente. OnParametersSet es el único hook que se
        // re-ejecuta en ese caso, y se ejecuta después de resincronizar los
        // filtros de arriba desde la URL, así que el modal parte de los
        // filtros ya vigentes en pantalla.
        if (Accion == "guardar-filtro")
        {
            _mensajeErrorFiltro = null;
            _mostrarGuardarFiltro = true;
        }
    }

    private async Task CambiarEstadoAsync(string? valor)
    {
        _estadoFiltro = valor ?? string.Empty;
        NavigationManager.ActualizarFiltroEnUrl("estado", valor);
        await RecargarAsync();
    }

    /// <summary>
    /// Número de la última carga pedida. Cada carga captura el suyo antes del
    /// <c>await</c> y, al volver, solo escribe estado si sigue siendo la
    /// vigente: si mientras tanto cambió un filtro, la búsqueda, la página o
    /// el orden, su respuesta es de otra pregunta. QuickGrid ya descarta las
    /// FILAS de una carga superada, pero no sabe nada de
    /// <see cref="_totalElementos"/>, <see cref="_elementosPagina"/> (de la que
    /// viven los atajos j/k/x y la selección) ni <see cref="_errorCarga"/>, que
    /// son de esta página. Mismo mecanismo que Gestiones.razor.cs.
    /// </summary>
    private int _cargaVigente;

    private async ValueTask<GridItemsProviderResult<TrabajadorListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<TrabajadorListaDto> request)
    {
        if (_servirPaginaEnMemoria)
        {
            // Refresco de una fila tras guardar en la vista rápida (ver RefrescarFilaAsync).
            _servirPaginaEnMemoria = false;
            return GridItemsProviderResult.From(_elementosPagina.ToList(), _totalElementos);
        }

        // Todo lo que define la pregunta se lee ANTES del await.
        var carga = ++_cargaVigente;
        var (ordenarPor, descendente) = LecturaOrden.Leer(request);
        (_ordenExportar, _descendenteExportar) = (ordenarPor, descendente);
        var consulta = new ObtenerTrabajadoresQuery(
            Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
            EmpresaId: Guid.TryParse(_filtroEmpresaId, out var empresaId) ? empresaId : null,
            SubcontrataId: Guid.TryParse(_filtroSubcontrataId, out var subcontrataId) ? subcontrataId : null,
            Pagina: (request.StartIndex / _paginacion.ItemsPerPage) + 1,
            TamanoPagina: _paginacion.ItemsPerPage,
            CentroId: Guid.TryParse(_filtroCentroId, out var centroId) ? centroId : null,
            // Esta página pinta el motivo del estado y «Registrados vigentes»: es quien pide el desglose.
            ConDesgloseDocumental: true,
            OrdenarPor: ordenarPor,
            Descendente: descendente,
            EstadoDocumental: string.IsNullOrWhiteSpace(_estadoFiltro) ? null : _estadoFiltro,
            ConRecuentosPorEstado: true);

        // La misma pregunta que la carga anterior conserva la selección y la fila enfocada (es el refresco
        // tras corregir una incidencia, ver RefrescarTrasCorreccionAsync); cualquier otra las limpia.
        var conservarSeleccion = consulta == _consultaQueConservaSeleccion;
        if (!conservarSeleccion)
            _consultaQueConservaSeleccion = null;
        _ultimaConsulta = consulta;

        _cargando = true;
        _errorCarga = false;

        try
        {
            var resultado = await Mediator.Send(consulta, request.CancellationToken);

            if (carga != _cargaVigente)
                return GridItemsProviderResult.From(new List<TrabajadorListaDto>(), 0);

            _totalElementos = resultado.TotalElementos;
            _recuentosPorEstado = resultado.RecuentosPorEstado;
            // El alcance cero solo lo fija VacioSegunAlcance con la lista vacía: con datos ya no aplica
            // (cambio de empresa o Asignación de Cartera concedida con la página abierta).
            if (_totalElementos > 0) _alcanceCero = false;

            var elementos = resultado.Elementos.ToList();
            _elementosPagina = elementos;
            if (conservarSeleccion)
            {
                // Lo que ya no está en la página (la corrección lo sacó del filtro) deja de estar seleccionado.
                _seleccionados.IntersectWith(elementos.Select(t => t.Id));
                if (_idEnfocado is { } enfocado && elementos.All(t => t.Id != enfocado))
                    _idEnfocado = null;
            }
            else
            {
                _seleccionados.Clear();
                _idEnfocado = null;
            }

            return GridItemsProviderResult.From(elementos, resultado.TotalElementos);
        }
        catch (Exception) when (carga != _cargaVigente)
        {
            // Una carga superada que falla (o que QuickGrid canceló) no es un
            // error de la vigente: no puede tapar su resultado.
            return GridItemsProviderResult.From(new List<TrabajadorListaDto>(), 0);
        }
        catch (Exception)
        {
            _errorCarga = true;
            return GridItemsProviderResult.From(new List<TrabajadorListaDto>(), 0);
        }
        finally
        {
            if (carga == _cargaVigente)
            {
                _cargando = false;
                StateHasChanged();
            }
        }
    }

    /// <summary>Un Id de la URL solo vale si es un Guid; cualquier otra cosa es «sin filtro».</summary>
    private static string IdDesdeUrl(string? valor) =>
        Guid.TryParse(valor, out var id) ? id.ToString() : string.Empty;

    /// <summary>
    /// Los dos parámetros del empleador en una sola navegación: son excluyentes,
    /// y dos navegaciones seguidas se pisarían (ver
    /// <see cref="NavigationManagerExtensions.ActualizarFiltrosEnUrl"/>).
    /// </summary>
    private void EscribirEmpleadorEnUrl() =>
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["empresa"] = _filtroEmpresaId,
            ["subcontrata"] = _filtroSubcontrataId,
        });

    private async Task FiltrarPorEmpresaAsync(string valor)
    {
        _filtroEmpresaId = valor;
        _filtroSubcontrataId = string.Empty;
        EscribirEmpleadorEnUrl();
        await RecargarAsync();
    }

    private async Task FiltrarPorSubcontrataAsync(string valor)
    {
        _filtroSubcontrataId = valor;
        _filtroEmpresaId = string.Empty;
        EscribirEmpleadorEnUrl();
        await RecargarAsync();
    }

    private async Task FiltrarPorCentroAsync(string valor)
    {
        _filtroCentroId = IdDesdeUrl(valor);
        NavigationManager.ActualizarFiltroEnUrl("centro", _filtroCentroId);
        await RecargarAsync();
    }

    private Task QuitarFiltroCentroAsync() => FiltrarPorCentroAsync(string.Empty);

    /// <summary>
    /// Opciones de la pastilla «Centro». Cada una dice de qué Cliente empresarial es el Centro: dos Clientes
    /// pueden tener un Centro con el mismo nombre.
    /// </summary>
    private IReadOnlyList<OpcionEstado> OpcionesFiltroCentro =>
        _centrosFiltro
            .Select(c => new OpcionEstado(c.Id.ToString(), Textos["ListaOpcionCentroDeCliente", c.Nombre, c.ClienteRazonSocial].Value))
            .ToList();

    /// <summary>
    /// Rótulo del chip de Centro. Si el Id filtrado no está entre los Centros cargados (fuera del alcance, dado de
    /// baja), «Centro» a secas, como <see cref="EtiquetaFiltroEmpresa"/>.
    /// </summary>
    private string EtiquetaFiltroCentro =>
        _centrosFiltro.FirstOrDefault(c => c.Id.ToString() == _filtroCentroId)?.Nombre ?? Textos["EtiquetaCentro"].Value;

    private async Task CargarCentrosFiltroAsync()
    {
        try
        {
            var centros = await Mediator.Send(new ObtenerCentrosParaSelectorQuery(), _ciclo.Token);
            if (!_desechado)
                _centrosFiltro = centros;
        }
        catch (OperationCanceledException) when (_ciclo.IsCancellationRequested)
        {
            // La página retirada no conserva una respuesta del contexto anterior.
        }
        catch (Exception)
        {
            if (!_desechado)
                _centrosFiltro = [];
        }
    }

    private async Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        NavigationManager.ActualizarFiltroEnUrl("q", valor);
        await RecargarAsync();
    }

    private bool HayFiltrosActivos =>
        !string.IsNullOrWhiteSpace(_busqueda) || !string.IsNullOrWhiteSpace(_estadoFiltro)
        || !string.IsNullOrWhiteSpace(_filtroEmpresaId) || !string.IsNullOrWhiteSpace(_filtroSubcontrataId)
        || !string.IsNullOrWhiteSpace(_filtroCentroId);

    // ── Corrección de una incidencia desde la ventana del motivo ────────────────────────────────

    private CaeManager.Web.Features.Documentos.Components.CorreccionIncidenciaDocumental _correccion = default!;

    /// <summary>La consulta de la última carga de la rejilla: la pregunta que hay en pantalla.</summary>
    private ObtenerTrabajadoresQuery? _ultimaConsulta;

    /// <summary>
    /// Si la siguiente carga hace esta misma pregunta (mismos filtros, orden y página), conserva la selección y
    /// la fila enfocada en vez de limpiarlas. La fija <see cref="RefrescarTrasCorreccionAsync"/> y vale solo para
    /// ese refresco: la sueltan una carga con otra pregunta y toda recarga que pida la página
    /// (<see cref="RecargarAsync"/>: alta, baja, reintento tras un error), aunque repita la pregunta.
    ///
    /// <para>
    /// No se suelta al leerla: si la corrección cambia el total (la fila corregida sale del filtro activo),
    /// QuickGrid vuelve a pedir la misma página por su cuenta en el render siguiente (ver
    /// <see cref="RecargarAsync"/>), y esa segunda petición es el mismo refresco, no una recarga nueva.
    /// </para>
    /// </summary>
    private ObtenerTrabajadoresQuery? _consultaQueConservaSeleccion;

    /// <summary>
    /// Abre, sin salir del listado, el formulario del documento de la incidencia pulsada. Solo llega aquí quien
    /// puede escribir (la ventana no ofrece botones a los demás); quién puede guardar lo decide el comando.
    /// </summary>
    private Task CorregirIncidenciaAsync(TrabajadorListaDto trabajador, CaeManager.Application.Documentos.IncidenciaDocumentalDto incidencia) =>
        _correccion.AbrirAsync(incidencia.DocumentoId, incidencia.TipoDocumentoId, trabajador.Id, null);

    /// <summary>
    /// Tras corregir una incidencia: se vuelve a pedir la página tal como está (mismos filtros, orden y página)
    /// para que la fila, su motivo, «vigentes/registrados» y los recuentos de la franja digan lo de ahora. La
    /// selección y la fila enfocada se conservan (mismo criterio que Centros.RefrescarTrasCorreccionAsync).
    /// </summary>
    private async Task RefrescarTrasCorreccionAsync()
    {
        if (_desechado || _grid is null)
            return;

        _consultaQueConservaSeleccion = _ultimaConsulta;
        await _grid.RefreshDataAsync();
        StateHasChanged();
    }

    /// <summary>
    /// Rótulo del chip de empresa. Si el id filtrado no está en el catálogo
    /// cargado —cartera que ya no lo alcanza, empresa dada de baja— se dice
    /// "Empresa" a secas en vez de pintar un GUID: el chip existe para que se
    /// pueda quitar el filtro, y para eso no hace falta saber su nombre.
    /// </summary>
    private string EtiquetaFiltroEmpresa =>
        _empleadoresFiltro.Empresas.FirstOrDefault(e => e.Id.ToString() == _filtroEmpresaId)?.RazonSocial ?? Textos["EtiquetaEmpresa"].Value;

    private string EtiquetaFiltroSubcontrata =>
        _empleadoresFiltro.Subcontratas.FirstOrDefault(s => s.Id.ToString() == _filtroSubcontrataId)?.RazonSocial ?? Textos["EtiquetaSubcontrata"].Value;

    /// <summary>Prefijos del valor de la pastilla «Empresa», que lleva dentro también las subcontratas.</summary>
    private const string PrefijoEmpresa = "empresa:";
    private const string PrefijoSubcontrata = "subcontrata:";

    /// <summary>
    /// Valor de la pastilla «Empresa»: el filtro de empresa o el de subcontrata (se excluyen), con su
    /// prefijo para que un mismo Id no pueda confundir los dos parámetros de la consulta.
    /// </summary>
    private string ValorFiltroEmpleador =>
        !string.IsNullOrWhiteSpace(_filtroSubcontrataId) ? PrefijoSubcontrata + _filtroSubcontrataId
        : !string.IsNullOrWhiteSpace(_filtroEmpresaId) ? PrefijoEmpresa + _filtroEmpresaId
        : string.Empty;

    /// <summary>
    /// Opciones de la pastilla «Empresa»: las empresas y, detrás, las subcontratas marcadas como tales; solo las que
    /// emplean a algún Trabajador visible (<see cref="_empleadoresFiltro"/>).
    /// </summary>
    private IReadOnlyList<OpcionEstado> OpcionesFiltroEmpleador =>
        _empleadoresFiltro.Empresas.Select(e => new OpcionEstado(PrefijoEmpresa + e.Id, e.RazonSocial))
            .Concat(_empleadoresFiltro.Subcontratas.Select(sc => new OpcionEstado(PrefijoSubcontrata + sc.Id, Textos["ListaOpcionSubcontrata", sc.RazonSocial].Value)))
            .ToList();

    private Task CambiarFiltroEmpleadorAsync(string valor) =>
        valor.StartsWith(PrefijoSubcontrata, StringComparison.Ordinal) ? FiltrarPorSubcontrataAsync(valor[PrefijoSubcontrata.Length..])
        : valor.StartsWith(PrefijoEmpresa, StringComparison.Ordinal) ? FiltrarPorEmpresaAsync(valor[PrefijoEmpresa.Length..])
        // «Todas»: FiltrarPorEmpresaAsync suelta también la subcontrata, en una sola recarga.
        : FiltrarPorEmpresaAsync(string.Empty);

    /// <summary>Los filtros guardados de esta pantalla, dentro de «Más filtros».</summary>
    private IReadOnlyList<OpcionEstado> OpcionesFiltrosGuardados =>
        _filtrosGuardados.Select(f => new OpcionEstado(f.Id.ToString(), f.Nombre)).ToList();

    /// <summary>«Guardar filtro» de «Más filtros»: abre el modal sin el error de un intento anterior.</summary>
    private void AbrirGuardarFiltro()
    {
        _mensajeErrorFiltro = null;
        _mostrarGuardarFiltro = true;
    }

    private Task QuitarFiltroBusquedaAsync() => BuscarAsync(string.Empty);

    private Task QuitarFiltroEmpresaAsync() => FiltrarPorEmpresaAsync(string.Empty);

    private Task QuitarFiltroSubcontrataAsync() => FiltrarPorSubcontrataAsync(string.Empty);

    /// <summary>
    /// Quita los cinco filtros en una sola recarga. Encadenar los setters
    /// lanzaría cinco consultas y las cuatro primeras devolverían listas que ya
    /// no se van a pintar. Los cinco viven además en la URL y se
    /// limpian allí en UNA sola navegación (ver
    /// <see cref="NavigationManagerExtensions.ActualizarFiltrosEnUrl"/>): si no,
    /// <see cref="OnParametersSetAsync"/> los repondría desde la URL
    /// en la siguiente pasada de parámetros.
    /// </summary>
    private async Task LimpiarFiltrosAsync()
    {
        _busqueda = string.Empty;
        _estadoFiltro = string.Empty;
        _filtroEmpresaId = string.Empty;
        _filtroSubcontrataId = string.Empty;
        _filtroCentroId = string.Empty;
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["q"] = null,
            ["estado"] = null,
            ["empresa"] = null,
            ["subcontrata"] = null,
            ["centro"] = null,
        });
        await RecargarAsync();
    }

    /// <summary>
    /// Vuelve a la página 1 y pide la lista UNA vez.
    /// <see cref="PaginationState.SetCurrentPageIndexAsync"/> avisa a QuickGrid
    /// siempre, cambie o no la página, y QuickGrid recarga al recibir el aviso
    /// (así funciona <see cref="CambiarPaginaAsync"/>). Llamar a los dos,
    /// aviso y <c>RefreshDataAsync</c>, lanzaba dos consultas idénticas por
    /// cada búsqueda o filtro aunque el total no cambiara (medido en
    /// <c>TrabajadoresListaGen2Tests</c>).
    ///
    /// <para>
    /// Lo que esto no evita: si la respuesta trae un total distinto del
    /// anterior, QuickGrid vuelve a pedir la misma página por su cuenta en el
    /// render siguiente (<c>QuickGrid.OnParametersSetAsync</c> →
    /// <c>RefreshDataCoreAsync</c>). Es de QuickGrid, no de esta página.
    /// </para>
    /// </summary>
    private async Task RecargarAsync()
    {
        // Una recarga pedida por la página no es el refresco tras corregir una incidencia: aunque repita la
        // pregunta, limpia la selección y la fila enfocada.
        _consultaQueConservaSeleccion = null;

        if (_grid is not null && _paginacion.CurrentPageIndex == 0)
            await _grid.RefreshDataAsync();
        else
            await _paginacion.SetCurrentPageIndexAsync(0);

        StateHasChanged();
    }

    private async Task CargarEmpleadoresFiltroAsync()
    {
        var version = Interlocked.Increment(ref _versionEmpleadores);
        try
        {
            var empleadores = await Mediator.Send(new ObtenerEmpleadoresDeTrabajadoresVisiblesQuery(), _ciclo.Token);
            if (!_desechado && version == Volatile.Read(ref _versionEmpleadores))
                _empleadoresFiltro = empleadores;
        }
        catch (OperationCanceledException) when (_ciclo.IsCancellationRequested)
        {
            // La página retirada no conserva una respuesta del contexto anterior.
        }
        catch (Exception)
        {
            if (!_desechado && version == Volatile.Read(ref _versionEmpleadores))
                _empleadoresFiltro = new EmpleadoresDeTrabajadoresDto([], []);
        }
    }

    private async Task RecargarTrasEscrituraAsync()
    {
        // Altas, bajas, restauraciones y Asignaciones pueden cambiar los empleadores legibles.
        await CargarEmpleadoresFiltroAsync();
        if (!_desechado)
            await RecargarAsync();
    }

    private async Task AbrirCrearAsync()
    {
        _empresasDisponibles = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery());
        _subcontratasDisponibles = await Mediator.Send(new ObtenerSubcontratasParaSelectorQuery());

        var perfil = await Mediator.Send(new ObtenerPerfilVocabularioActualQuery());
        _resolverEmpresaEnSilencio = perfil == PerfilVocabularioTenant.ClienteDirecto && _empresasDisponibles.Count == 1;

        // El filtro representa visibilidad de lectura; solo se prellena con un empleador
        // que siga disponible en el selector del alta recién cargado.
        if (!string.IsNullOrWhiteSpace(_filtroSubcontrataId))
        {
            _tipoEmpleador = "subcontrata";
            _subcontrataId = _subcontratasDisponibles.FirstOrDefault(s => s.Id.ToString() == _filtroSubcontrataId)?.Id.ToString() ?? string.Empty;
            _empresaId = string.Empty;
        }
        else if (_resolverEmpresaEnSilencio)
        {
            _tipoEmpleador = "empresa";
            _empresaId = _empresasDisponibles[0].Id.ToString();
            _subcontrataId = string.Empty;
        }
        else
        {
            _tipoEmpleador = "empresa";
            _empresaId = _empresasDisponibles.FirstOrDefault(e => e.Id.ToString() == _filtroEmpresaId)?.Id.ToString() ?? string.Empty;
            _subcontrataId = string.Empty;
        }
        _dni = string.Empty;
        _nombre = string.Empty;
        _apellidos = string.Empty;
        _alias = string.Empty;
        _puesto = string.Empty;
        _fechaNacimiento = string.Empty;
        _email = string.Empty;
        _telefono = string.Empty;
        _observaciones = string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
        FijarInstantaneaFormulario();
    }

    private readonly InstantaneaFormulario _instantanea = new();
    private readonly InstantaneaFormulario _instantaneaAsignarCentro = new();

    /// <summary>
    /// P1-E2b: «hay cambios» del drawer de alta de Trabajador, comparado con cómo se abrió; cerrado (también tras
    /// guardar) nunca hay nada que perder. Lo lee DrawerFormulario, que lleva dentro el guardián de cerrar y de navegar.
    /// </summary>
    private bool HayCambiosAlta => _drawerVisible && _instantanea.Difiere(ValoresFormulario());

    /// <summary>
    /// P1-E2b: «hay cambios» del modal de asignar a centro, que no es un formulario de kit: sus campos comparados con cómo se
    /// abrió. Lo lee AvisoCambiosSinGuardar; cerrado nunca hay nada que perder. «Guardar filtro» (ModalFormulario) lleva el suyo
    /// en <see cref="HayCambiosFiltro"/>.
    /// </summary>
    private bool HayCambiosSinGuardar =>
        _asignarCentroVisible && _instantaneaAsignarCentro.Difiere(ValoresAsignarCentro());

    /// <summary>«Guardar filtro» abre siempre con el nombre vacío: hay algo que perder en cuanto se ha escrito uno.</summary>
    private bool HayCambiosFiltro => _mostrarGuardarFiltro && !string.IsNullOrWhiteSpace(_nombreFiltroNuevo);

    private object?[] ValoresFormulario() =>
        [_tipoEmpleador, _empresaId, _subcontrataId, _dni, _nombre, _apellidos, _alias, _puesto, _fechaNacimiento, _email, _telefono, _observaciones];

    private object?[] ValoresAsignarCentro() => [_centroIdParaAsignar, _fechaAltaParaAsignar];

    private void FijarInstantaneaFormulario() => _instantanea.Fijar(ValoresFormulario());

    private void CerrarFormulariosDescartando() => CerrarAsignarCentro();

    /// <summary>Cancelar descarta el nombre: reabrir el modal sin tocarlo no es un cambio.</summary>
    private void CerrarModalGuardarFiltro()
    {
        _mostrarGuardarFiltro = false;
        _nombreFiltroNuevo = string.Empty;
        _mensajeErrorFiltro = null;
    }

    private void SeleccionarTipoEmpresa() => CambiarTipoEmpleador("empresa");

    private void SeleccionarTipoSubcontrata() => CambiarTipoEmpleador("subcontrata");

    private void CambiarTipoEmpleador(string tipo)
    {
        _tipoEmpleador = tipo;
        _empresaId = string.Empty;
        _subcontrataId = string.Empty;
        LimpiarAvisoDeEmpleadorFaltante();
    }

    // D-05: el aviso «Selecciona una empresa/subcontrata.» era de un intento anterior de guardar;
    // al elegir el empleador deja de ser verdad y no puede quedarse en pantalla hasta el siguiente guardado.
    private void AlElegirEmpresa(string valor)
    {
        _empresaId = valor;
        LimpiarAvisoDeEmpleadorFaltante();
    }

    private void AlElegirSubcontrata(string valor)
    {
        _subcontrataId = valor;
        LimpiarAvisoDeEmpleadorFaltante();
    }

    /// <summary>Solo retira los avisos de empleador sin elegir: un error de servidor (DNI duplicado…) sigue en pantalla.</summary>
    private void LimpiarAvisoDeEmpleadorFaltante()
    {
        if (_mensajeErrorFormulario == Textos["ErrorSeleccionaEmpresa"]
            || _mensajeErrorFormulario == Textos["ErrorSeleccionaSubcontrata"])
            _mensajeErrorFormulario = null;
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        return Task.CompletedTask;
    }

    private async Task GuardarAsync()
    {
        // Guarda de doble clic: un segundo «Guardar» antes de que vuelva el
        // primero daría de alta el mismo trabajador dos veces (o chocaría con
        // el DNI duplicado). El botón se deshabilita con Cargando, pero el
        // segundo clic puede llegar antes de ese render.
        if (_guardando) return;
        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();

        try
        {
            var email = string.IsNullOrWhiteSpace(_email) ? null : _email;
            var telefono = string.IsNullOrWhiteSpace(_telefono) ? null : _telefono;
            var observaciones = string.IsNullOrWhiteSpace(_observaciones) ? null : _observaciones;
            var alias = string.IsNullOrWhiteSpace(_alias) ? null : _alias;
            var puesto = string.IsNullOrWhiteSpace(_puesto) ? null : _puesto;
            var fechaNacimiento = DateOnly.TryParse(_fechaNacimiento, out var fecha) ? fecha : (DateOnly?)null;

            Guid? empresaId = null;
            Guid? subcontrataId = null;

            if (_tipoEmpleador == "empresa")
            {
                if (!Guid.TryParse(_empresaId, out var empresaIdValor))
                {
                    _mensajeErrorFormulario = Textos["ErrorSeleccionaEmpresa"];
                    return;
                }
                empresaId = empresaIdValor;
            }
            else
            {
                if (!Guid.TryParse(_subcontrataId, out var subcontrataIdValor))
                {
                    _mensajeErrorFormulario = Textos["ErrorSeleccionaSubcontrata"];
                    return;
                }
                subcontrataId = subcontrataIdValor;
            }

            var resultado = await Mediator.Send(
                new CrearTrabajadorCommand(empresaId, subcontrataId, _nombre, _apellidos, _dni, fechaNacimiento, email, observaciones, alias, telefono, puesto));

            if (resultado.EsFallido)
            {
                _mensajeErrorFormulario = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar(Textos["ToastCreado"], TonoToast.Exito);
            _drawerVisible = false;
            await RecargarTrasEscrituraAsync();
        }
        catch (ValidationException ex)
        {
            _erroresCampo = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _mensajeErrorFormulario = Textos["ErrorGuardar"];
        }
        finally
        {
            _guardando = false;
        }
    }

    private string? ObtenerError(string campo) => _erroresCampo.GetValueOrDefault(campo);

    /// <summary>
    /// Validación inline al salir del campo (mismo patrón que Centros.razor,
    /// Project-Hydra-Negocio/tecnico/docs/archive/design/UX_PATTERNS.md, P1-18 de Project-Hydra-Negocio/MATURITY_REVIEW.md). El
    /// empleador (empresa/subcontrata) no se valida aquí — IncludeProperties
    /// restringe la validación al campo que perdió el foco, así que null
    /// para ambos no afecta el resultado de estas reglas.
    /// </summary>
    private Task ValidarDniAsync() => ValidarCampoAsync(nameof(CrearTrabajadorCommand.Dni));

    private Task ValidarNombreAsync() => ValidarCampoAsync(nameof(CrearTrabajadorCommand.Nombre));

    private Task ValidarApellidosAsync() => ValidarCampoAsync(nameof(CrearTrabajadorCommand.Apellidos));

    private Task ValidarAliasAsync() => ValidarCampoAsync(nameof(CrearTrabajadorCommand.Alias));

    private Task ValidarPuestoAsync() => ValidarCampoAsync(nameof(CrearTrabajadorCommand.Puesto));

    private Task ValidarEmailAsync() => ValidarCampoAsync(nameof(CrearTrabajadorCommand.Email));

    private Task ValidarTelefonoAsync() => ValidarCampoAsync(nameof(CrearTrabajadorCommand.Telefono));

    private async Task ValidarCampoAsync(string campo)
    {
        var email = string.IsNullOrWhiteSpace(_email) ? null : _email;
        var telefono = string.IsNullOrWhiteSpace(_telefono) ? null : _telefono;
        var observaciones = string.IsNullOrWhiteSpace(_observaciones) ? null : _observaciones;
        var alias = string.IsNullOrWhiteSpace(_alias) ? null : _alias;
        var puesto = string.IsNullOrWhiteSpace(_puesto) ? null : _puesto;
        var fechaNacimiento = DateOnly.TryParse(_fechaNacimiento, out var fecha) ? fecha : (DateOnly?)null;

        var resultado = await ValidadorCrear.ValidateAsync(
            new CrearTrabajadorCommand(null, null, _nombre, _apellidos, _dni, fechaNacimiento, email, observaciones, alias, telefono, puesto),
            opciones => opciones.IncludeProperties(campo));

        if (resultado.IsValid)
            _erroresCampo.Remove(campo);
        else
            _erroresCampo[campo] = resultado.Errors[0].ErrorMessage;
    }

    private string? PistaDni
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_dni)) return null;

            var resultado = ValidadorIdentificacion.Analizar(_dni);
            return resultado.Tipo switch
            {
                TipoIdentificacion.Dni => resultado.EsValido ? Textos["PistaDniValido"] : Textos["PistaDniInvalido"],
                TipoIdentificacion.Nie => resultado.EsValido ? Textos["PistaNieValido"] : Textos["PistaNieInvalido"],
                TipoIdentificacion.NifEmpresa => resultado.EsValido ? Textos["PistaCifValido"] : Textos["PistaCifInvalido"],
                TipoIdentificacion.TieSoporte => Textos["PistaTieSoporte"],
                _ => Textos["PistaDocumentoExtranjero"]
            };
        }
    }

    private string ToneDni
    {
        get
        {
            var resultado = ValidadorIdentificacion.Analizar(_dni);
            if (resultado.Tipo is TipoIdentificacion.TieSoporte or TipoIdentificacion.Otros) return "info";
            return resultado.EsValido ? "exito" : "error";
        }
    }

    private static string NombreCompleto(TrabajadorListaDto trabajador) => $"{trabajador.Nombre} {trabajador.Apellidos}";

    /// <summary>Porcentaje de documentos registrados al día para la barra de «Registrados vigentes»; sin documentos no hay universo y la barra pinta la raya.</summary>
    private static int? PorcentajeRegistradosVigentes(TrabajadorListaDto trabajador) =>
        trabajador.DocumentosRegistrados <= 0 ? null : trabajador.DocumentosVigentes * 100 / trabajador.DocumentosRegistrados;

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
        if (_eliminandoLote) return;
        _eliminandoLote = true;

        try
        {
            var idsPedidos = _seleccionados.ToList();
            var resultado = await Mediator.Send(new EliminarTrabajadoresCommand(idsPedidos));
            var dto = resultado.Valor;

            // FS-09: el aviso ofrece «Deshacer» sobre los que sí cayeron.
            IReadOnlyList<Guid> eliminados = dto.IdsEliminados ?? [];

            ToastService.Mostrar(
                dto.Errores.Count == 0
                    ? Textos["ToastLoteEliminados", dto.Eliminados]
                    : Textos["ToastLoteEliminadosConErrores", dto.Eliminados, dto.Errores.Count, string.Join(" ", dto.Errores)],
                dto.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia,
                eliminados.Count > 0 ? Textos["ToastAccionDeshacer"].Value : null,
                eliminados.Count > 0 ? () => DeshacerEliminarLoteAsync(eliminados) : null);

            // Se retiran solo las fichas de los que cayeron (IdsEliminados); un superviviente
            // conserva la suya y su edición sin guardar. Sin ids en el DTO se retiran todas las
            // pedidas: mejor pasarse de retirar que dejar abierta una ficha muerta.
            if (dto.Eliminados > 0)
                WorkspaceService.RetirarSiEstaAbierto(EntidadWorkspace.Trabajador, dto.IdsEliminados ?? idsPedidos);

            _seleccionados.Clear();
            _confirmarEliminarLoteVisible = false;
            await RecargarTrasEscrituraAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["ErrorEliminarLote"], TonoToast.Error);
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
            var r = await RestauracionEnLote.RestaurarAsync(ids, id => Mediator.Send(new RestaurarTrabajadorCommand(id)));

            ToastService.Mostrar(
                r.Errores.Count == 0 ? Textos["ToastLoteRestaurados", r.Restaurados].Value : Textos["ToastLoteRestauradosConErrores", r.Restaurados, r.Errores.Count, string.Join(" ", r.Errores)].Value,
                r.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);

            if (r.Restaurados > 0)
                await RecargarTrasEscrituraAsync();
        }
        finally
        {
            _restaurandoLote = false;
        }
    }

    // --- Fase B: "Asignar a centro…" en lote ---

    private async Task AbrirAsignarCentroAsync()
    {
        // Antes del await: una consulta de faltantes del diálogo anterior que
        // siga en vuelo ya no es de este.
        ++_faltantesVigente;
        _centrosDisponiblesParaAsignar = await Mediator.Send(new ObtenerCentrosParaSelectorQuery());
        _centroIdParaAsignar = string.Empty;
        _fechaAltaParaAsignar = DiaDeNegocio.Hoy().ToString("yyyy-MM-dd");
        _documentosFaltantesParaAsignar = [];
        _asignarCentroVisible = true;
        // La fecha de alta de hoy viene puesta: no es un cambio de quien edita.
        _instantaneaAsignarCentro.Fijar(ValoresAsignarCentro());
    }

    /// <summary>
    /// Única salida del diálogo (Cancelar, la X, Escape, clic fuera y el
    /// cierre tras asignar): invalida la consulta de faltantes en vuelo y
    /// suelta el aviso, que era del centro elegido en este diálogo.
    /// </summary>
    private void CerrarAsignarCentro()
    {
        ++_faltantesVigente;
        _asignarCentroVisible = false;
        _centroIdParaAsignar = string.Empty;
        _documentosFaltantesParaAsignar = [];
    }

    /// <summary>
    /// Carga vigente del aviso de documentos faltantes. Elegir un centro y
    /// después otro lanza dos consultas; si la del primero vuelve la última,
    /// sin esto el aviso hablaría del centro que ya no está elegido — y el
    /// botón diría «Asignar igualmente» (o «Asignar») por el centro equivocado.
    /// Abrir y cerrar el diálogo también la invalidan: cerrar con la consulta
    /// del centro A en vuelo y reabrir no puede pintar los faltantes de A en
    /// un diálogo nuevo que todavía no tiene centro.
    /// </summary>
    private int _faltantesVigente;

    private async Task CambiarCentroParaAsignarAsync(string valor)
    {
        var carga = ++_faltantesVigente;
        _centroIdParaAsignar = valor;

        if (!Guid.TryParse(valor, out var centroId))
        {
            _documentosFaltantesParaAsignar = [];
            return;
        }

        var faltantes = await Mediator.Send(
            new ObtenerDocumentosFaltantesParaAsignacionQuery(_seleccionados.ToList(), [centroId]));

        if (carga == _faltantesVigente)
            _documentosFaltantesParaAsignar = faltantes;
    }

    private async Task ConfirmarAsignarCentroAsync()
    {
        if (_asignandoLote) return;

        if (!Guid.TryParse(_centroIdParaAsignar, out var centroId))
        {
            ToastService.Mostrar(Textos["ErrorSeleccionaCentro"], TonoToast.Error);
            return;
        }

        if (!DateOnly.TryParse(_fechaAltaParaAsignar, out var fechaAlta))
        {
            ToastService.Mostrar(Textos["ErrorFechaAltaInvalida"], TonoToast.Error);
            return;
        }

        _asignandoLote = true;

        try
        {
            var resultado = await Mediator.Send(new CrearAsignacionesCommand(_seleccionados.ToList(), [centroId], fechaAlta));

            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            var dto = resultado.Valor;
            var resumen = dto.YaActivas > 0
                ? Textos["ToastAsignacionesCreadasConActivas", dto.Creadas, dto.YaActivas].Value
                : Textos["ToastAsignacionesCreadas", dto.Creadas].Value;
            ToastService.Mostrar(resumen, dto.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);
            // Sin esto, un rechazo con motivo (p. ej. Asignaciones que solapan
            // un periodo ya registrado, DEC-19) solo cambiaba el tono del
            // toast de arriba a Advertencia, sin decir nunca por qué —
            // mismo patrón que DrawerAsignacionMasiva.razor.cs.
            foreach (var error in dto.Errores)
                ToastService.Mostrar(error, TonoToast.Advertencia);

            _seleccionados.Clear();
            CerrarAsignarCentro();
            await RecargarTrasEscrituraAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["ErrorAsignarLote"], TonoToast.Error);
        }
        finally
        {
            _asignandoLote = false;
        }
    }

    // --- P3-31: atajos de teclado j/k/x/Enter ---

    /// <summary>
    /// La fila se tinta por su estado documental: Vencido en rojo, Urgente en ámbar (rediseño de
    /// listados, fase 1, mismos tokens que Clientes empresariales, Empresas y Centros). El foco de
    /// teclado (j/k) se suma al tinte, no lo sustituye (list-page.css combina las dos clases).
    /// </summary>
    private string ObtenerClaseFila(TrabajadorListaDto item)
    {
        var tinte = item.EstadoDocumental switch
        {
            EstadoDocumento.Faltante or EstadoDocumento.Vencido => "fila-tintada-peligro",
            EstadoDocumento.Urgente => "fila-tintada-aviso",
            _ => null
        };
        var foco = item.Id == _idEnfocado ? "fila-enfocada" : null;
        // «fila-pulsable»: el clic en la fila abre la vista rápida (atajos-lista.js).
        return string.Join(' ', new[] { "fila-pulsable", foco, tinte }.Where(c => c is not null));
    }

    private async Task ManejarAtajoAsync(string tecla)
    {
        // «e» no depende de que haya filas: sin fila enfocada edita la ficha que esté abierta,
        // aunque el filtro haya dejado la lista vacía.
        if (tecla == "e")
        {
            if ((_idEnfocado ?? TrabajadorEnVistaPrevia) is { } idEditar)
                await AbrirVistaRapidaEnEdicionAsync(idEditar);
            return;
        }

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
                // Como en la maqueta, «x» enciende la selección múltiple: la casilla marcada queda a
                // la vista, y la barra de lote nunca apunta a una fila sin casilla. Sin escritura
                // (o sin alcance) no hay selección que ofrecer.
                if (_idEnfocado is { } idAlternar && _puedeEscribir && !_alcanceCero)
                {
                    _seleccionMultiple = true;
                    AlternarSeleccion(idAlternar, !_seleccionados.Contains(idAlternar));
                }
                break;
            case "Enter":
                if (_idEnfocado is { } idAbrir)
                    await AbrirVistaRapidaAsync(idAbrir);
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

        var valores = JsonSerializer.Deserialize<FiltrosTrabajadoresJson>(filtro.ValoresJson);
        if (valores is null) return;

        _busqueda = valores.Busqueda ?? string.Empty;
        _filtroSubcontrataId = IdDesdeUrl(valores.SubcontrataId);
        _filtroEmpresaId = _filtroSubcontrataId.Length > 0 ? string.Empty : IdDesdeUrl(valores.EmpresaId);
        // Un filtro guardado define el conjunto entero: sin estado guardado, el estado se quita. Un valor que
        // ya no es una opción (catálogo cambiado) se ignora, como uno de la URL.
        _estadoFiltro = EstadoDocumentoUi.SeleccionDocumentalValida(valores.Estado);
        _filtroCentroId = IdDesdeUrl(valores.CentroId);

        // Los cinco filtros del filtro guardado se escriben también en la URL, en una sola navegación.
        // Si solo se aplicaran en memoria, la siguiente navegación dentro de la página haría que
        // OnParametersSetAsync los borrase leyendo una URL sin ellos.
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["q"] = _busqueda,
            ["estado"] = _estadoFiltro,
            ["empresa"] = _filtroEmpresaId,
            ["subcontrata"] = _filtroSubcontrataId,
            ["centro"] = _filtroCentroId,
        });
        await RecargarAsync();
    }

    private async Task GuardarFiltroActualAsync()
    {
        if (string.IsNullOrWhiteSpace(_nombreFiltroNuevo) || _guardandoFiltro) return;

        _guardandoFiltro = true;
        _mensajeErrorFiltro = null;

        try
        {
            var valoresJson = JsonSerializer.Serialize(new FiltrosTrabajadoresJson(
                string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                string.IsNullOrWhiteSpace(_filtroEmpresaId) ? null : _filtroEmpresaId,
                string.IsNullOrWhiteSpace(_filtroSubcontrataId) ? null : _filtroSubcontrataId,
                string.IsNullOrWhiteSpace(_estadoFiltro) ? null : _estadoFiltro,
                string.IsNullOrWhiteSpace(_filtroCentroId) ? null : _filtroCentroId));

            var resultado = await Mediator.Send(
                new GuardarFiltroCommand(PantallasConFiltrosGuardados.Trabajadores, _nombreFiltroNuevo, valoresJson));

            if (resultado.EsFallido)
            {
                _mensajeErrorFiltro = resultado.Error.Mensaje;
                return;
            }

            _filtrosGuardados = await Mediator.Send(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Trabajadores));
            _mostrarGuardarFiltro = false;
            _nombreFiltroNuevo = string.Empty;
            ToastService.Mostrar(Textos["ToastFiltroGuardado"], TonoToast.Exito);
        }
        finally
        {
            _guardandoFiltro = false;
        }
    }

    private void PedirEliminarFiltroGuardado(string idTexto) =>
        _filtroGuardadoAEliminar = _filtrosGuardados.FirstOrDefault(f => f.Id.ToString() == idTexto);

    private async Task ConfirmarEliminarFiltroGuardadoAsync()
    {
        if (_eliminandoFiltroGuardado || _filtroGuardadoAEliminar is not { } filtro)
            return;

        _eliminandoFiltroGuardado = true;
        try
        {
            var resultado = await Mediator.Send(new EliminarFiltroGuardadoCommand(filtro.Id));
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            // Ya está borrado: se cierra el diálogo y se quita de la lista sin releerla, para que
            // un fallo de la relectura no deje la confirmación abierta sobre un filtro que no existe.
            _filtroGuardadoAEliminar = null;
            _filtrosGuardados = _filtrosGuardados.Where(f => f.Id != filtro.Id).ToList();
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["FiltroGuardadoBorrarError"], TonoToast.Error);
        }
        finally
        {
            _eliminandoFiltroGuardado = false;
        }
    }

    /// <summary>El orden de la última carga de la rejilla, para que «Exportar esta vista» salga en el mismo.</summary>
    private string? _ordenExportar;
    private bool _descendenteExportar;

    /// <summary>
    /// Los criterios de «Exportar esta vista»: los mismos que esta página pasa a la consulta del
    /// listado, con los nombres de parámetro del endpoint de exportación, que los lee igual.
    /// </summary>
    private Dictionary<string, string?> CriteriosExportar => new()
    {
        ["q"] = _busqueda,
        ["estado"] = _estadoFiltro,
        ["empresa"] = _filtroEmpresaId,
        ["subcontrata"] = _filtroSubcontrataId,
        ["centro"] = _filtroCentroId,
        ["orden"] = _ordenExportar,
        ["desc"] = _descendenteExportar ? "true" : null,
    };
}
