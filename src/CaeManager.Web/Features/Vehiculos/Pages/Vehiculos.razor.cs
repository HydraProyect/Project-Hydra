using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Vehiculos.Commands.CrearVehiculo;
using CaeManager.Application.Vehiculos.Commands.EliminarVehiculos;
using CaeManager.Application.Vehiculos.Commands.RestaurarVehiculo;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculos;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Documentos;
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

namespace CaeManager.Web.Features.Vehiculos.Pages;

public partial class Vehiculos : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    /// <summary>Quien mira no alcanza nada en este Tenant (<see cref="CaeManager.Web.Features.IncorporacionCartera.Components.VacioSegunAlcance"/>):
    /// sin «+ Nuevo» en cabecera, para no duplicar lo que quizá ya existe fuera de su cartera.</summary>
    private bool _alcanceCero;

    /// <summary>Estado 4a del mockup del selector: hay que elegir una empresa de la cartera antes de ver la lista.</summary>
    private bool _sinEmpresaSeleccionada;

    /// <summary>La empresa activa aún no se ha resuelto: se pinta una carga en vez de la lista.</summary>
    private bool _resolviendoEmpresa = true;

    private int _pagina = 1;
    private int _tamanoPagina = 20;
    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_totalElementos / (double)TamanoPedido));
    private bool _cargaPendiente = true;
    private bool _opcionesOrdenVehiculo;
    private readonly HashSet<Guid> _expandidos = [];
    // Filas que la búsqueda abriría por una incidencia coincidente y que se cerraron a mano.
    private readonly HashSet<Guid> _cerradasAMano = [];
    private CancellationTokenSource? _peticionActiva;
    private string _campoOrdenVehiculo = nameof(VehiculoListaDto.Nombre);

    private Task CambiarPaginaAsync(int pagina)
    {
        _pagina = pagina;
        _expandidos.Clear();
        _cerradasAMano.Clear();
        return CargarListaAsync();
    }

    private Task CambiarTamanoPaginaAsync(int tamano)
    {
        _tamanoPagina = tamano;
        return RecargarAsync();
    }

    private string TituloColumnaVehiculo => Textos["ListaColumnaVehiculoOrden",
        Textos[_campoOrdenVehiculo == nameof(VehiculoListaDto.NumeroPlaca) ? "EtiquetaMatricula" : "EtiquetaNombre"].Value].Value;

    [CascadingParameter] private Task<AuthenticationState>? EstadoAutenticacion { get; set; }
    private bool _puedeEscribir;

    private string _busqueda = string.Empty;

    /// <summary>Ya corrió la primera sincronización de los filtros con la URL (OnParametersSet): antes no se carga la lista.</summary>
    private bool _parametrosInicializados;
    private string _estadoFiltro = string.Empty;
    private string _filtroEmpresaId = string.Empty;
    private string _filtroSubcontrataId = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    /// <summary>Filas por estado para la franja, sin el filtro de estado aplicado. <c>null</c> hasta la primera carga.</summary>
    private IReadOnlyDictionary<string, int>? _recuentosPorEstado;

    private IReadOnlyList<EmpresaSelectorDto> _empresasDisponibles = [];
    private IReadOnlyList<SubcontrataSelectorDto> _subcontratasDisponibles = [];

    private bool _drawerVisible;
    private string _tipoEmpleador = "empresa";

    // DDL-076: en perfil Cliente Directo con una única Empresa, el selector
    // de Empresa no aparece — se resuelve en silencio. Mismo mecanismo que
    // Trabajadores.razor.cs.
    private bool _resolverEmpresaEnSilencio;

    private string _empresaId = string.Empty;
    private string _subcontrataId = string.Empty;
    private string _nombre = string.Empty;
    private string _modelo = string.Empty;
    private string _numeroPlaca = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    // Vista rápida (patrón de listados): clic en la fila, su nombre o Enter abren el panel de
    // 520 px del Context Workspace (mismo patrón que Empresas y Trabajadores). A la página
    // Vehículo 360 (/vehiculos/{id}) se va con el icono 360 de la fila o con el del panel.
    private Task AbrirVistaRapidaAsync(Guid id) =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Vehiculo, id, NombreDe(id), "informacion");

    /// <summary>
    /// Tecla «e»: la vista rápida de la fila enfocada, ya en edición (el lápiz de la cabecera
    /// del panel). Si el rol no puede escribir, el panel se abre y se queda en lectura.
    /// </summary>
    private Task AbrirVistaRapidaEnEdicionAsync(Guid id) =>
        WorkspaceService.AbrirEnEdicionAsync(EntidadWorkspace.Vehiculo, id, NombreDe(id));

    // El Vehículo del panel puede no estar en la página (el filtro lo dejó fuera): su nombre
    // es entonces el del frame abierto.
    private string NombreDe(Guid id) =>
        _elementosPagina.FirstOrDefault(v => v.Id == id) is { } vehiculo
            ? vehiculo.Nombre
            : WorkspaceService.FrameActual is { } frame && frame.EntidadId == id ? frame.TituloVisible : string.Empty;

    /// <summary>Vehículo cuyo panel está abierto arriba de la pila del Context Workspace, si lo hay.</summary>
    private Guid? VehiculoEnVistaPrevia =>
        WorkspaceService.FrameActual is { Tipo: EntidadWorkspace.Vehiculo } frame ? frame.EntidadId : null;

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
    private List<VehiculoListaDto> _elementosPagina = [];
    private Guid? _idEnfocado;
    private bool _eliminandoLote;
    private bool _confirmarEliminarLoteVisible;

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

    /// <summary>Agrupación de la URL (<c>?agrupar=empresa</c>): «Por Empresa», como la maqueta de Vehículos.</summary>
    [SupplyParameterFromQuery(Name = "agrupar")]
    public string? AgruparInicial { get; set; }

    private bool _agruparPorEmpresa;
    private const string ValorAgruparPorEmpresa = "empresa";

    /// <summary>Orden de columna (<c>?orden=matricula-desc</c>). Sin él, el de fábrica: ver <see cref="_orden"/>.</summary>
    [SupplyParameterFromQuery(Name = "orden")]
    public string? OrdenInicial { get; set; }

    /// <summary>
    /// El orden de columna viaja en la URL y forma parte de la vista. La rejilla nace sin ordenar
    /// (el orden de fábrica es el de la consulta); «vehiculo» y «matricula» son la misma columna.
    /// </summary>
    private readonly OrdenDeRejilla _orden = new(
    [
        ("vehiculo", nameof(VehiculoListaDto.Nombre)),
        ("modelo", nameof(VehiculoListaDto.Modelo)),
        ("matricula", nameof(VehiculoListaDto.NumeroPlaca)),
        ("empleador", nameof(VehiculoListaDto.EmpleadorNombre)),
        ("documentacion", nameof(VehiculoListaDto.EstadoDocumental)),
    ]);

    /// <summary>El orden que llega (URL, filtro guardado o vista recordada). Si cambia, la rejilla se remonta ya ordenada.</summary>
    private bool LeerOrden(string? valor)
    {
        if (!_orden.Leer(valor))
            return false;

        // Sin orden (de fábrica, p. ej. tras «Restablecer vista») la cabecera vuelve a «Vehículo · Nombre», como al abrir la pantalla.
        if (_orden.Propiedad is null)
            _campoOrdenVehiculo = nameof(VehiculoListaDto.Nombre);
        else if (_orden.Propiedad is nameof(VehiculoListaDto.Nombre) or nameof(VehiculoListaDto.NumeroPlaca))
            _campoOrdenVehiculo = _orden.Propiedad;
        return true;
    }

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ITenantActual TenantActual { get; set; } = default!;
    [Inject] private IValidator<CrearVehiculoCommand> ValidadorCrear { get; set; } = default!;


    /// <summary>
    /// Se cancela al retirarse la página: las consultas en curso dejan de
    /// trabajar para nadie y ninguna respuesta tardía repinta un componente ya
    /// desechado. Mismo patrón que <c>Documentos.razor.cs</c>. Su Token se lee
    /// SIEMPRE antes del primer await del método que lo usa.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    /// <summary>
    /// Número de la última carga de la rejilla. Cada carga captura el suyo
    /// ANTES del await y, al volver, solo escribe estado si sigue siendo la
    /// vigente: sin esto, la respuesta de una búsqueda ya abandonada podía
    /// pisar el total, las filas, la selección o el foco de la pregunta que sí
    /// se está mirando.
    /// </summary>
    private int _cargaVigente;

    /// <summary>La respuesta es de la pregunta vigente y la página sigue viva.</summary>
    private bool EsVigente(int carga) => !_desechado && carga == _cargaVigente;

    // ── La fila se refresca tras guardar en la vista rápida ─────────────────────────────────────
    // El panel vive en MainLayout y guarda sin pasar por esta página: avisa por
    // ContextWorkspaceService.OnEntidadGuardada. Se vuelve a pedir SOLO esa fila y se sustituye
    // en sitio (mismo criterio que Centros.RefrescarCentroAsync): filtros, orden, página,
    // selección, fila enfocada y desplazamiento no se tocan, y la fila permanece aunque el
    // cambio la saque del filtro activo, hasta la siguiente carga. Los recuentos de la franja
    // tampoco se recalculan hasta entonces.

    private void AlGuardarEntidad(EntidadWorkspace tipo, Guid id)
    {
        if (tipo == EntidadWorkspace.Vehiculo)
            _ = InvokeAsync(() => RefrescarFilaAsync(id));
    }

    private async Task RefrescarFilaAsync(Guid id)
    {
        // Con una carga en vuelo no se sustituye nada: la sustitución caería sobre una página que
        // está a punto de cambiar. Hueco conocido: si esa carga leyó antes de que el guardado
        // fuera firme, la fila conserva el dato anterior hasta la siguiente carga.
        if (_desechado || _cargando || !_elementosPagina.Any(e => e.Id == id))
            return;

        var carga = _cargaVigente;
        try
        {
            // Misma pregunta de estado que la carga de página (ConRecuentosPorEstado): sin ella el
            // handler toma el camino que deja sin estado a quien no tiene documentos, y la fila
            // pasaría de «Sin incidencias» a «Sin documentos» al refrescarla. Y el desglose se pide
            // igual que allí: sin él la fila refrescada perdería el motivo bajo la pastilla.
            var resultado = await Mediator.Send(
                new ObtenerVehiculosQuery(
                    Busqueda: null, ConRecuentosPorEstado: true, VehiculoId: id, ConDesgloseDocumental: true),
                _ciclo.Token);
            var indice = _elementosPagina.FindIndex(e => e.Id == id);
            if (_desechado || _cargando || carga != _cargaVigente || indice < 0
                || resultado.Elementos.FirstOrDefault() is not { } actualizada)
                return;

            _elementosPagina[indice] = actualizada;
            StateHasChanged();
        }
        catch (Exception)
        {
            // El guardado ya es firme: que falle la relectura no es un error que enseñar. La
            // fila conserva el dato anterior hasta la siguiente carga, como antes de este aviso.
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

    protected override async Task OnInitializedAsync()
    {
        WorkspaceService.OnEntidadGuardada += AlGuardarEntidad;
        if (EstadoAutenticacion is not null)
        {
            var usuario = (await EstadoAutenticacion).User;
            if (_desechado) return;
            _puedeEscribir = Roles.ConEscrituraCsv.Split(',').Any(usuario.IsInRole);
        }

        // Hasta resolver la empresa activa no se monta la lista ni sus acciones: con la consulta en
        // vuelo el render saldría con «hay empresa» y lanzaría la carga del Tenant de origen.
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

        _empresasDisponibles = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery());
        _subcontratasDisponibles = await Mediator.Send(new ObtenerSubcontratasParaSelectorQuery());
    }

    /// <summary>
    /// Se re-ejecuta en cada navegación dentro de la propia página (recargar,
    /// compartir la URL, volver atrás) — no solo en el primer render — para
    /// que el filtro de la URL sea la fuente de verdad, no solo su semilla
    /// inicial (P1-18 de Project-Hydra-Negocio/MATURITY_REVIEW.md).
    /// </summary>
    protected override void OnParametersSet()
    {
        // Retirada la página con la resolución en vuelo, ComponentBase aún invoca esto: no se procesan
        // parámetros de la URL de un componente que ya no existe, ni en el estado 4a.
        if (_desechado || _resolviendoEmpresa || _sinEmpresaSeleccionada)
            return;

        // Todo filtro que la URL cambia exige recargar la lista, igual que el orden y el agrupado más abajo. Antes
        // solo lo exigían estos dos: si un render llegaba mientras OnInitializedAsync seguía esperando sus
        // consultas de selectores (con _resolviendoEmpresa ya en false pero esta primera sincronización con la URL
        // aún sin ejecutar), OnAfterRender cargaba la lista SIN filtros y la sincronización posterior ponía la
        // búsqueda sin pedir otra carga: la página quedaba mostrando el listado entero con ?q=… en la URL
        // (VistaRecordadaE2ETests.Vehiculos_…, intermitente en la cola de fusión).
        var deLaUrl = TerminoBusquedaInicial ?? string.Empty;
        if (deLaUrl != _busqueda)
        {
            _busqueda = deLaUrl;
            _cargaPendiente = true;
        }

        var estadoDeLaUrl = EstadoDocumentoUi.SeleccionDocumentalValida(EstadoInicial);
        if (estadoDeLaUrl != _estadoFiltro)
        {
            _estadoFiltro = estadoDeLaUrl;
            _cargaPendiente = true;
        }

        var subcontrataDeLaUrl = IdDesdeUrl(SubcontrataInicial);
        var empresaDeLaUrl = subcontrataDeLaUrl.Length > 0 ? string.Empty : IdDesdeUrl(EmpresaInicial);
        if (subcontrataDeLaUrl != _filtroSubcontrataId)
        {
            _filtroSubcontrataId = subcontrataDeLaUrl;
            _cargaPendiente = true;
        }

        if (empresaDeLaUrl != _filtroEmpresaId)
        {
            _filtroEmpresaId = empresaDeLaUrl;
            _cargaPendiente = true;
        }

        _parametrosInicializados = true;

        if (LeerOrden(OrdenInicial)) _cargaPendiente = true;

        var agruparDeLaUrl = string.Equals(AgruparInicial, ValorAgruparPorEmpresa, StringComparison.OrdinalIgnoreCase);
        if (agruparDeLaUrl != _agruparPorEmpresa)
        {
            _agruparPorEmpresa = agruparDeLaUrl;
            _cargaPendiente = true;
        }
    }

    /// <summary>Un Id de la URL solo vale si es un Guid; cualquier otra cosa es «sin filtro».</summary>
    private static string IdDesdeUrl(string? valor) =>
        Guid.TryParse(valor, out var id) ? id.ToString() : string.Empty;

    /// <summary>
    /// Los dos parámetros del empleador en una sola navegación: son excluyentes,
    /// y dos navegaciones seguidas se pisarían (ver <c>ActualizarFiltrosEnUrl</c>).
    /// </summary>
    private void EscribirEmpleadorEnUrl() =>
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["empresa"] = _filtroEmpresaId,
            ["subcontrata"] = _filtroSubcontrataId,
        });

    private async Task CambiarEstadoAsync(string? valor)
    {
        _estadoFiltro = valor ?? string.Empty;
        NavigationManager.ActualizarFiltroEnUrl("estado", valor);
        await RecargarAsync();
    }

    private async Task CargarListaAsync()
    {
        if (_desechado) return;
        _cargaPendiente = false;
        // Todo lo que define la pregunta —el número de carga y el token— se lee
        // ANTES del await. Leer _ciclo.Token después dejaría que un Dispose
        // intermedio lo hubiera desechado.
        var carga = ++_cargaVigente;

        // Dos motivos para cancelar: que la página se retire (el ciclo) y que
        // se pida otra página, otro orden u otro filtro.
        using var cancelacion = CancellationTokenSource.CreateLinkedTokenSource(_ciclo.Token);
        var anterior = _peticionActiva;
        _peticionActiva = cancelacion;
        anterior?.Cancel();
        var token = cancelacion.Token;

        // La pregunta se compone entera ANTES del await, como el número de carga y el token.
        // Agrupar «Por Empresa» es ordenar por la Empresa (solo cambia el sentido si se pidió así).
        var ordenarPor = _agruparPorEmpresa ? nameof(VehiculoListaDto.EmpleadorNombre) : _orden.Propiedad;
        var descendente = _orden.Direccion == SortDirection.Descending
            && (!_agruparPorEmpresa || _orden.Propiedad == nameof(VehiculoListaDto.EmpleadorNombre));
        var consulta = new ObtenerVehiculosQuery(
            Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
            EmpresaId: Guid.TryParse(_filtroEmpresaId, out var empresaId) ? empresaId : null,
            SubcontrataId: Guid.TryParse(_filtroSubcontrataId, out var subcontrataId) ? subcontrataId : null,
            Pagina: _pagina,
            TamanoPagina: TamanoPedido,
            OrdenarPor: ordenarPor,
            Descendente: descendente,
            EstadoDocumental: string.IsNullOrWhiteSpace(_estadoFiltro) ? null : _estadoFiltro,
            ConRecuentosPorEstado: true,
            // Esta página pinta el motivo del estado: es quien pide el desglose.
            ConDesgloseDocumental: true);

        // La misma pregunta que la carga anterior conserva la selección y la fila enfocada (es el refresco
        // tras corregir una incidencia, ver RefrescarTrasCorreccionAsync); cualquier otra las limpia.
        var conservarSeleccion = consulta == _consultaQueConservaSeleccion;
        _consultaQueConservaSeleccion = null;
        _ultimaConsulta = consulta;

        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            if (!_agruparPorEmpresa && _orden.Anotar(ordenarPor, descendente))
                NavigationManager.ActualizarFiltroEnUrl("orden", _orden.EnUrl);
            (_ordenExportar, _descendenteExportar) = (ordenarPor, descendente);

            var resultado = await Mediator.Send(consulta, token);

            // La respuesta de una búsqueda ya abandonada no puede pisar el
            // total, las filas ni la selección de la pregunta que sí se está
            // mirando: solo la generación vigente escribe el estado de la página.
            if (!EsVigente(carga))
                return;

            _totalElementos = resultado.TotalElementos;
            _recuentosPorEstado = resultado.RecuentosPorEstado;

            var elementos = resultado.Elementos.ToList();
            _elementosPagina = elementos;
            if (conservarSeleccion)
            {
                // Lo que ya no está en la página (la corrección lo sacó del filtro) deja de estar seleccionado.
                var pintadas = FilasPintadas;
                _seleccionados.IntersectWith(pintadas.Select(v => v.Id));
                if (_idEnfocado is { } enfocado && pintadas.All(v => v.Id != enfocado))
                    _idEnfocado = null;
            }
            else
            {
                _seleccionados.Clear();
                _expandidos.Clear();
                _cerradasAMano.Clear();
                _idEnfocado = null;
            }

            _expandidos.IntersectWith(elementos.Where(v => v.Incidencias.Count > 0).Select(v => v.Id));
            if (_pagina > TotalPaginas)
            {
                _pagina = TotalPaginas;
                await CargarListaAsync();
            }
        }
        catch (Exception) when (!EsVigente(carga))
        {
            // Una carga superada que falla no es un error de la vigente: no
            // puede tapar su resultado con el estado de error de una pregunta
            // que ya nadie hace.
            return;
        }
        catch (Exception)
        {
            _errorCarga = true;
            return;
        }
        finally
        {
            if (ReferenceEquals(_peticionActiva, cancelacion)) _peticionActiva = null;
            if (EsVigente(carga))
            {
                _cargando = false;
                StateHasChanged();
            }
        }
    }

    private async Task CambiarAgrupacionAsync(string? valor)
    {
        _agruparPorEmpresa = valor == ValorAgruparPorEmpresa;
        // Al agrupar todo nace plegado: la selección y el foco previos apuntarían a filas que ya no se ven.
        _seleccionados.Clear(); _idEnfocado = null;
        NavigationManager.ActualizarFiltroEnUrl("agrupar", _agruparPorEmpresa ? ValorAgruparPorEmpresa : null);
        await RecargarAsync();
    }

    private string ValorAgrupacion => _agruparPorEmpresa ? ValorAgruparPorEmpresa : string.Empty;

    private IReadOnlyList<OpcionEstado> OpcionesAgrupacion =>
        [new OpcionEstado(ValorAgruparPorEmpresa, Textos["AgruparPorEmpresa"].Value)];

    /// <summary>Porcentaje de documentos al día para la barra de la columna Documentación; sin documentos no hay universo y la barra pinta la raya.</summary>
    private static int? PorcentajeDocumentacion(VehiculoListaDto vehiculo) =>
        vehiculo.DocumentosRegistrados <= 0 ? null : vehiculo.DocumentosVigentes * 100 / vehiculo.DocumentosRegistrados;

    /// <summary>Cuántos Vehículos de la Empresa hay en esta página (agrupar es ordenar en servidor: el recuento es el de la página).</summary>
    /// <summary>Los grupos abiertos de la vista: todos nacen plegados y se recuerdan hasta recargar.</summary>
    private readonly GruposAbiertos _gruposAbiertos = new();

    /// <summary>
    /// Las filas que se ven: sin agrupar, las de la página; agrupada, solo las de los grupos abiertos. La selección masiva,
    /// el foco y los atajos operan sobre estas, nunca sobre filas ocultas por un grupo plegado.
    /// </summary>
    private List<VehiculoListaDto> FilasPintadas =>
        _agruparPorEmpresa ? _elementosPagina.Where(v => _gruposAbiertos.EstaAbierto(v.EmpleadorNombre)).ToList() : _elementosPagina;

    /// <summary>Abre o pliega un grupo; al plegarlo, sus filas dejan de estar seleccionadas y enfocadas.</summary>
    private void AlternarGrupo(string clave)
    {
        _gruposAbiertos.Alternar(clave);
        var pintadas = FilasPintadas;
        _seleccionados.IntersectWith(pintadas.Select(v => v.Id));
        if (_idEnfocado is { } enfocado && pintadas.All(v => v.Id != enfocado)) _idEnfocado = null;
    }

    /// <summary>Las filas con la cabecera del grupo «Por Empresa» en la primera de cada uno; los grupos plegados las ocultan.</summary>
    private IEnumerable<FilaDeGrupo<VehiculoListaDto>> FilasConGrupo() =>
        GruposDeLista.Agrupar(_elementosPagina, _agruparPorEmpresa, v => v.EmpleadorNombre, (clave, _) => clave, EsFilaRoja, _gruposAbiertos);

    /// <summary>Fila en rojo: lo que falta o vence (el tinte de peligro de la fila).</summary>
    private static bool EsFilaRoja(VehiculoListaDto item) => item.EstadoDocumental is EstadoDocumento.Faltante or EstadoDocumento.Vencido;

    /// <summary>Filas que se piden por página: agrupada, todas las filtradas, para que el tamaño de página no corte los grupos.</summary>
    private int TamanoPedido => AgrupacionDeLista.TamanoPedido(_agruparPorEmpresa, _tamanoPagina);

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

    private async Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        _cerradasAMano.Clear();
        NavigationManager.ActualizarFiltroEnUrl("q", valor);
        await RecargarAsync();
    }

    private const string PrefijoEmpresa = "empresa:";
    private const string PrefijoSubcontrata = "subcontrata:";

    private string ValorFiltroEmpleador =>
        !string.IsNullOrWhiteSpace(_filtroSubcontrataId) ? PrefijoSubcontrata + _filtroSubcontrataId
        : !string.IsNullOrWhiteSpace(_filtroEmpresaId) ? PrefijoEmpresa + _filtroEmpresaId
        : string.Empty;

    private IReadOnlyList<OpcionEstado> OpcionesFiltroEmpleador =>
        _empresasDisponibles.Select(e => new OpcionEstado(PrefijoEmpresa + e.Id, e.RazonSocial))
            .Concat(_subcontratasDisponibles.Select(s => new OpcionEstado(PrefijoSubcontrata + s.Id, Textos["ListaOpcionSubcontrata", s.RazonSocial].Value)))
            .ToList();

    private Task CambiarFiltroEmpleadorAsync(string valor) =>
        valor.StartsWith(PrefijoSubcontrata, StringComparison.Ordinal) ? FiltrarPorSubcontrataAsync(valor[PrefijoSubcontrata.Length..])
        : valor.StartsWith(PrefijoEmpresa, StringComparison.Ordinal) ? FiltrarPorEmpresaAsync(valor[PrefijoEmpresa.Length..])
        : FiltrarPorEmpresaAsync(string.Empty);

    private string EtiquetaFiltroEmpresa =>
        _empresasDisponibles.FirstOrDefault(e => e.Id.ToString() == _filtroEmpresaId)?.RazonSocial ?? Textos["EtiquetaEmpresa"].Value;

    private string EtiquetaFiltroSubcontrata =>
        _subcontratasDisponibles.FirstOrDefault(s => s.Id.ToString() == _filtroSubcontrataId)?.RazonSocial ?? Textos["EtiquetaSubcontrata"].Value;

    private async Task OrdenarAsync(string propiedad, bool? descendente = null)
    {
        _opcionesOrdenVehiculo = false;
        var clave = propiedad switch
        {
            nameof(VehiculoListaDto.Nombre) => "vehiculo",
            nameof(VehiculoListaDto.Modelo) => "modelo",
            nameof(VehiculoListaDto.EmpleadorNombre) => "empleador",
            nameof(VehiculoListaDto.NumeroPlaca) => "matricula",
            _ => "documentacion",
        };
        var desc = descendente ?? (_orden.Propiedad == propiedad && _orden.Direccion != SortDirection.Descending);
        LeerOrden(clave + (desc ? "-desc" : string.Empty));
        // Ordenar por una columna es dejar de agrupar «Por Empresa» (como en Trabajadores).
        _agruparPorEmpresa = false;
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["orden"] = _orden.EnUrl,
            ["agrupar"] = null,
        });
        await RecargarAsync();
    }

    private async Task CambiarCampoOrdenVehiculoAsync(string valor)
    {
        if (valor is not (nameof(VehiculoListaDto.Nombre) or nameof(VehiculoListaDto.NumeroPlaca)) || valor == _campoOrdenVehiculo) return;
        var desc = _orden.Es("vehiculo", "matricula") && _orden.Direccion == SortDirection.Descending;
        _campoOrdenVehiculo = valor;
        _opcionesOrdenVehiculo = false;
        await OrdenarAsync(valor, desc);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Sin la primera sincronización con la URL no se carga: el primer render, hecho mientras OnInitializedAsync
        // sigue esperando sus selectores, ya ve _resolviendoEmpresa en false y pediría la lista SIN los filtros de la
        // URL (misma guarda que Trabajadores).
        if (_cargaPendiente && _parametrosInicializados && !_resolviendoEmpresa && !_sinEmpresaSeleccionada && !_desechado)
            await CargarListaAsync();

    }

    private bool TodosExpandidos => _elementosPagina.Any(v => v.Incidencias.Count > 0)
        && _elementosPagina.Where(v => v.Incidencias.Count > 0).All(EstaExpandido);

    private void AlternarExpansion(Guid id)
    {
        var vehiculo = _elementosPagina.FirstOrDefault(v => v.Id == id);
        if (vehiculo is not null && EstaExpandido(vehiculo))
        {
            _expandidos.Remove(id);
            _cerradasAMano.Add(id);
        }
        else
        {
            _cerradasAMano.Remove(id);
            _expandidos.Add(id);
        }
    }

    private void AlternarTodosExpandidos(bool expandir)
    {
        _expandidos.Clear();
        _cerradasAMano.Clear();
        var conIncidencias = _elementosPagina.Where(v => v.Incidencias.Count > 0).Select(v => v.Id);
        if (expandir) _expandidos.UnionWith(conIncidencias);
        else _cerradasAMano.UnionWith(conIncidencias);
    }

    private bool EstaExpandido(VehiculoListaDto vehiculo) => _expandidos.Contains(vehiculo.Id)
        || (!_cerradasAMano.Contains(vehiculo.Id) && vehiculo.Incidencias.Any(i => CaeManager.Web.Features.Documentos.Components.SubcajaIncidenciasDocumentales.Coincide(i, _busqueda)));

    /// <summary>
    /// Los cuatro filtros de la barra. Separa "aún no hay vehículos" de
    /// "ninguno con estos filtros": ofrecer "crea el primero" a quien acaba de
    /// filtrar lo manda a duplicar un vehículo que ya existe.
    /// </summary>
    private bool HayFiltrosActivos =>
        !string.IsNullOrWhiteSpace(_busqueda) || !string.IsNullOrWhiteSpace(_estadoFiltro)
        || !string.IsNullOrWhiteSpace(_filtroEmpresaId) || !string.IsNullOrWhiteSpace(_filtroSubcontrataId);

    /// <summary>
    /// Quita los cuatro filtros en una sola recarga. Todos viven en la
    /// URL y se limpian TAMBIÉN allí: <see cref="OnParametersSet"/> re-sincroniza
    /// desde la URL en cada navegación dentro de la página, así que dejarlos
    /// puestos los devolvería en cuanto el router volviera a pasar.
    /// </summary>
    private async Task LimpiarFiltrosAsync()
    {
        _busqueda = string.Empty;
        _estadoFiltro = string.Empty;
        _filtroEmpresaId = string.Empty;
        _filtroSubcontrataId = string.Empty;
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["q"] = null,
            ["estado"] = null,
            ["empresa"] = null,
            ["subcontrata"] = null,
        });
        await RecargarAsync();
    }

    // ── Corrección de una incidencia desde la ventana del motivo ────────────────────────────────

    private CaeManager.Web.Features.Documentos.Components.CorreccionIncidenciaDocumental _correccion = default!;

    /// <summary>La consulta de la última carga de la rejilla: la pregunta que hay en pantalla.</summary>
    private ObtenerVehiculosQuery? _ultimaConsulta;

    /// <summary>
    /// Si la siguiente carga hace esta misma pregunta (mismos filtros, orden y página), conserva la selección y
    /// la fila enfocada en vez de limpiarlas. La fija <see cref="RefrescarTrasCorreccionAsync"/> y vale solo para
    /// ese refresco: la sueltan una carga con otra pregunta y toda recarga que pida la página
    /// (<see cref="RecargarAsync"/>: alta, baja, reintento tras un error), aunque repita la pregunta.
    ///
    /// <para>
    /// No se suelta al leerla: si la corrección cambia el total (la fila corregida sale del filtro activo),
    /// La rejilla propia pide una página por corrección; el recibo se consume al componer esa petición.
    /// </para>
    /// </summary>
    private ObtenerVehiculosQuery? _consultaQueConservaSeleccion;

    /// <summary>
    /// Abre, sin salir del listado, el formulario del documento de la incidencia pulsada. Una incidencia es
    /// siempre un documento que existe, así que basta su Id: el formulario lo abre en renovación y lee de él a
    /// qué Vehículo pertenece. Solo llega aquí quien puede escribir (la ventana no ofrece botones a los demás);
    /// quién puede guardar lo decide el comando.
    /// </summary>
    private Task CorregirIncidenciaAsync(CaeManager.Application.Documentos.IncidenciaDocumentalDto incidencia) =>
        _correccion.AbrirAsync(incidencia.DocumentoId, incidencia.TipoDocumentoId, null, null);

    /// <summary>
    /// Tras corregir una incidencia: se vuelve a pedir la página tal como está (mismos filtros, orden y página)
    /// para que la fila, su motivo y los recuentos de la franja digan lo de ahora. La selección y la fila
    /// enfocada se conservan.
    /// </summary>
    private async Task RefrescarTrasCorreccionAsync()
    {
        if (_desechado)
            return;

        _consultaQueConservaSeleccion = _ultimaConsulta;
        await CargarListaAsync();
        StateHasChanged();
    }

    // ---- Filtros guardados (pieza compartida FiltrosGuardadosDeListado) ----

    private const string PantallaDeFiltrosGuardados =
        CaeManager.Application.Configuracion.Commands.GuardarFiltro.PantallasConFiltrosGuardados.Vehiculos;

    /// <summary>
    /// Lista blanca de los parámetros de VISTA de la URL: lo que guarda y aplica un filtro guardado, y lo que
    /// recuerda la vista recordada (<see cref="VistaRecordadaDeListado"/>), menos la búsqueda libre (<c>q</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> ParametrosDeVista = ["q", "estado", "empresa", "subcontrata", "orden", "agrupar"];

    private readonly ConexionFiltrosGuardados _filtrosGuardados = new();
    private readonly ConexionVistaRecordada _vistaRecordada = new();

    /// <summary>
    /// Un filtro guardado define la vista entera: lo que no trae se quita. Cada valor pasa por la misma
    /// validación que el de la URL en <see cref="OnParametersSet"/> (un Id que no es Guid o un estado que ya
    /// no existe se ignoran; con Empresa y subcontrata gana la subcontrata). La URL se escribe en una sola
    /// navegación y se recarga aquí: <see cref="OnParametersSet"/> sincroniza los campos y marca la carga como necesaria; la hace OnAfterRender.
    /// </summary>
    private async Task AplicarVistaGuardadaAsync(IReadOnlyDictionary<string, string?> vista)
    {
        _busqueda = vista.GetValueOrDefault("q") ?? string.Empty;
        _estadoFiltro = EstadoDocumentoUi.SeleccionDocumentalValida(vista.GetValueOrDefault("estado"));
        _filtroSubcontrataId = IdDesdeUrl(vista.GetValueOrDefault("subcontrata"));
        _filtroEmpresaId = _filtroSubcontrataId.Length > 0 ? string.Empty : IdDesdeUrl(vista.GetValueOrDefault("empresa"));
        LeerOrden(vista.GetValueOrDefault("orden"));
        _agruparPorEmpresa = vista.GetValueOrDefault("agrupar") == ValorAgruparPorEmpresa;
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["q"] = _busqueda,
            ["estado"] = _estadoFiltro,
            ["empresa"] = _filtroEmpresaId,
            ["subcontrata"] = _filtroSubcontrataId,
            ["orden"] = _orden.EnUrl,
            ["agrupar"] = _agruparPorEmpresa ? ValorAgruparPorEmpresa : null,
        });

        await RecargarAsync();
    }

    /// <summary>Vuelve a la página 1 y pide una sola página de propietarios.</summary>
    private async Task RecargarAsync()
    {
        _consultaQueConservaSeleccion = null;
        _pagina = 1;
        await CargarListaAsync();
        StateHasChanged();
    }

    private async Task AbrirCrearAsync()
    {
        _empresasDisponibles = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery());
        _subcontratasDisponibles = await Mediator.Send(new ObtenerSubcontratasParaSelectorQuery());

        var perfil = await Mediator.Send(new ObtenerPerfilVocabularioActualQuery());
        _resolverEmpresaEnSilencio = perfil == PerfilVocabularioTenant.ClienteDirecto && _empresasDisponibles.Count == 1;

        // Si la lista ya está filtrada por Empresa o Subcontrata, se presupone
        // que el vehículo que se va a dar de alta es de ese mismo empleador.
        if (!string.IsNullOrWhiteSpace(_filtroSubcontrataId))
        {
            _tipoEmpleador = "subcontrata";
            _subcontrataId = _filtroSubcontrataId;
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
            _empresaId = _filtroEmpresaId;
            _subcontrataId = string.Empty;
        }
        _nombre = string.Empty;
        _modelo = string.Empty;
        _numeroPlaca = string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
        FijarInstantaneaFormulario();
    }

    private readonly InstantaneaFormulario _instantanea = new();

    /// <summary>
    /// P1-E2b: único punto de verdad de «hay cambios» en el drawer de alta de Vehículo. Lo lee
    /// DrawerFormulario, que lleva dentro el guardián de cerrar y de salir de la página; cerrado
    /// (también tras guardar) nunca hay nada que perder.
    /// </summary>
    private bool HayCambiosSinGuardar => _drawerVisible && _instantanea.Difiere(ValoresFormulario());

    private object?[] ValoresFormulario() => [_tipoEmpleador, _empresaId, _subcontrataId, _nombre, _modelo, _numeroPlaca];

    private void FijarInstantaneaFormulario() => _instantanea.Fijar(ValoresFormulario());

    private void SeleccionarTipoEmpresa() => CambiarTipoEmpleador("empresa");

    private void SeleccionarTipoSubcontrata() => CambiarTipoEmpleador("subcontrata");

    private void CambiarTipoEmpleador(string tipo)
    {
        _tipoEmpleador = tipo;
        // En modo silencioso la Empresa única no tiene selector: al volver a «Empresa» hay que restaurarla.
        _empresaId = tipo == "empresa" && _resolverEmpresaEnSilencio && _empresasDisponibles.Count == 1
            ? _empresasDisponibles[0].Id.ToString()
            : string.Empty;
        _subcontrataId = string.Empty;
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
                new CrearVehiculoCommand(empresaId, subcontrataId, _nombre, _modelo, _numeroPlaca));

            if (resultado.EsFallido)
            {
                _mensajeErrorFormulario = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar(Textos["ToastCreado"], TonoToast.Exito);
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
    /// Project-Hydra-Negocio/tecnico/docs/archive/design/UX_PATTERNS.md, P1-18 de Project-Hydra-Negocio/MATURITY_REVIEW.md).
    /// </summary>
    private Task ValidarNombreAsync() => ValidarCampoAsync(nameof(CrearVehiculoCommand.Nombre));

    private Task ValidarModeloAsync() => ValidarCampoAsync(nameof(CrearVehiculoCommand.Modelo));

    private Task ValidarNumeroPlacaAsync() => ValidarCampoAsync(nameof(CrearVehiculoCommand.NumeroPlaca));

    private async Task ValidarCampoAsync(string campo)
    {
        var resultado = await ValidadorCrear.ValidateAsync(
            new CrearVehiculoCommand(null, null, _nombre, _modelo, _numeroPlaca),
            opciones => opciones.IncludeProperties(campo));

        if (resultado.IsValid)
            _erroresCampo.Remove(campo);
        else
            _erroresCampo[campo] = resultado.Errors[0].ErrorMessage;
    }

    private bool TodosSeleccionados =>
        FilasPintadas.Count > 0 && FilasPintadas.All(e => _seleccionados.Contains(e.Id));

    private void AlternarSeleccionTodos(bool marcar)
    {
        if (marcar)
            foreach (var elemento in FilasPintadas) _seleccionados.Add(elemento.Id);
        else
            _seleccionados.Clear();
    }

    private void AlternarSeleccion(Guid id, bool marcado)
    {
        if (marcado) _seleccionados.Add(id);
        else _seleccionados.Remove(id);
    }

    private bool _restaurandoLote;

    /// <summary>«Deshacer» del aviso de una eliminación en lote: un único deshacer restaura todos los que el lote sí eliminó.</summary>
    private async Task DeshacerEliminarLoteAsync(IReadOnlyList<Guid> ids)
    {
        if (_restaurandoLote) return;
        _restaurandoLote = true;

        try
        {
            var r = await RestauracionEnLote.RestaurarAsync(ids, id => Mediator.Send(new RestaurarVehiculoCommand(id)));

            ToastService.Mostrar(
                r.Errores.Count == 0 ? Textos["ToastLoteRestaurados", r.Restaurados].Value : Textos["ToastLoteRestauradosConErrores", r.Restaurados, r.Errores.Count, string.Join(" ", r.Errores)].Value,
                r.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);

            if (r.Restaurados > 0)
                await RecargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["ErrorRestaurar"], TonoToast.Error);
        }
        finally
        {
            _restaurandoLote = false;
        }
    }

    private async Task ConfirmarEliminarLoteAsync()
    {
        if (_eliminandoLote) return;
        _eliminandoLote = true;

        try
        {
            var idsPedidos = _seleccionados.ToList();
            var resultado = await Mediator.Send(new EliminarVehiculosCommand(idsPedidos));
            var dto = resultado.Valor;
            IReadOnlyList<Guid> eliminados = dto.IdsEliminados ?? [];

            ToastService.Mostrar(
                dto.Errores.Count == 0
                    ? Textos["ToastLoteEliminados", dto.Eliminados]
                    : Textos["ToastLoteEliminadosConErrores", dto.Eliminados, dto.Errores.Count, string.Join(" ", dto.Errores)],
                dto.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia,
                eliminados.Count > 0 ? Textos["ToastAccionDeshacer"].Value : null,
                eliminados.Count > 0 ? () => DeshacerEliminarLoteAsync(eliminados) : null);

            // Se retiran solo las fichas de los que cayeron (IdsEliminados); un superviviente
            // conserva la suya y su edición sin guardar.
            if (dto.Eliminados > 0)
                WorkspaceService.RetirarSiEstaAbierto(EntidadWorkspace.Vehiculo, dto.IdsEliminados ?? idsPedidos);

            _seleccionados.Clear();
            _confirmarEliminarLoteVisible = false;
            await RecargarAsync();
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

    private string ObtenerClaseFila(VehiculoListaDto item)
    {
        var tinte = item.EstadoDocumental switch
        {
            EstadoDocumento.Faltante or EstadoDocumento.Vencido => "fila-tintada-peligro",
            EstadoDocumento.Urgente => "fila-tintada-aviso",
            _ => null
        };
        var foco = item.Id == _idEnfocado ? "fila-enfocada" : null;
        // «fila-pulsable»: atajos-lista.js es quien, al
        // hacer clic en cualquier punto de la fila que no sea un control, pulsa su nombre.
        return string.Join(' ', new[] { "fila-pulsable", foco, tinte }.Where(c => c is not null));
    }

    private async Task ManejarAtajoAsync(string tecla)
    {
        // «e» no depende de que haya filas: sin fila enfocada edita la ficha que esté abierta,
        // aunque el filtro haya dejado la lista vacía.
        if (tecla == "e")
        {
            if ((_idEnfocado ?? VehiculoEnVistaPrevia) is { } idEditar)
                await AbrirVistaRapidaEnEdicionAsync(idEditar);
            return;
        }

        // El foco y el teclado solo recorren las filas pintadas (las de un grupo plegado no se ven).
        var pintadas = FilasPintadas;
        if (pintadas.Count == 0) return;

        switch (tecla)
        {
            case "j":
                {
                    var indiceActual = _idEnfocado is null ? -1 : pintadas.FindIndex(e => e.Id == _idEnfocado);
                    _idEnfocado = pintadas[Math.Min(indiceActual + 1, pintadas.Count - 1)].Id;
                    break;
                }
            case "k":
                {
                    var indiceActual = _idEnfocado is null ? 0 : pintadas.FindIndex(e => e.Id == _idEnfocado);
                    _idEnfocado = pintadas[Math.Max(indiceActual - 1, 0)].Id;
                    break;
                }
            case "x":
                if (_idEnfocado is { } idAlternar)
                    AlternarSeleccion(idAlternar, !_seleccionados.Contains(idAlternar));
                break;
            case "Enter":
                if (_idEnfocado is { } idAbrir)
                    await AbrirVistaRapidaAsync(idAbrir);
                break;
        }

        StateHasChanged();
    }

    // ---- Exportación de la vista filtrada ----

    private string? _ordenExportar;
    private bool _descendenteExportar;

    /// <summary>
    /// Los criterios de la vista con los nombres de parámetro de <c>/vehiculos/exportar.xlsx</c>:
    /// los mismos que <see cref="CargarListaAsync"/> pasa a la consulta del listado.
    /// </summary>
    private Dictionary<string, string?> CriteriosExportar => new()
    {
        ["q"] = _busqueda,
        ["estado"] = _estadoFiltro,
        ["empresa"] = _filtroEmpresaId,
        ["subcontrata"] = _filtroSubcontrataId,
        ["orden"] = _ordenExportar,
        ["desc"] = _descendenteExportar ? "true" : null,
    };
}
