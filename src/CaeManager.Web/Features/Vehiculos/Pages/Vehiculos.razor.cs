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

    private readonly PaginationState _paginacion = new() { ItemsPerPage = 20 };

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

    private QuickGrid<VehiculoListaDto>? _grid;
    private TemplateColumn<VehiculoListaDto>? _columnaVehiculo;
    private ColumnBase<VehiculoListaDto>? _ultimaColumnaOrden;
    private bool _ultimoOrdenAscendente = true;
    private string _campoOrdenVehiculo = nameof(VehiculoListaDto.Nombre);
    private bool _ordenVehiculoPendiente;
    private SortDirection _direccionOrdenPendiente;
    private static readonly GridSort<VehiculoListaDto> OrdenPorNombre = GridSort<VehiculoListaDto>.ByAscending(v => v.Nombre);
    private static readonly GridSort<VehiculoListaDto> OrdenPorModelo = GridSort<VehiculoListaDto>.ByAscending(v => v.Modelo);

    private GridSort<VehiculoListaDto> OrdenColumnaVehiculo =>
        _campoOrdenVehiculo == nameof(VehiculoListaDto.Modelo) ? OrdenPorModelo : OrdenPorNombre;

    private string TituloColumnaVehiculo => Textos["ListaColumnaVehiculoOrden",
        Textos[_campoOrdenVehiculo == nameof(VehiculoListaDto.Modelo) ? "EtiquetaModelo" : "EtiquetaNombre"].Value].Value;

    [CascadingParameter] private Task<AuthenticationState>? EstadoAutenticacion { get; set; }
    private bool _puedeEscribir;

    private string _busqueda = string.Empty;
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

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ITenantActual TenantActual { get; set; } = default!;
    [Inject] private IValidator<CrearVehiculoCommand> ValidadorCrear { get; set; } = default!;

    private GridItemsProvider<VehiculoListaDto>? _proveedorElementos;

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

    /// <summary>
    /// La siguiente petición de QuickGrid se sirve de <see cref="_elementosPagina"/> sin consultar:
    /// QuickGrid solo repinta sus filas cuando su proveedor le entrega una página, y una carga
    /// de verdad limpiaría la selección y la fila enfocada. El total no cambia, así que no hay
    /// segunda petición (ver RecargarAsync).
    /// </summary>
    private bool _servirPaginaEnMemoria;

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
        if (_desechado || _grid is null || _cargando || !_elementosPagina.Any(e => e.Id == id))
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

        // Delegado estable — ver Clientes.razor.cs (bucle de recargas de QuickGrid).
        _proveedorElementos = ProveerElementosAsync;

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

        var deLaUrl = TerminoBusquedaInicial ?? string.Empty;
        if (deLaUrl != _busqueda)
            _busqueda = deLaUrl;

        var estadoDeLaUrl = EstadoDocumentoUi.SeleccionDocumentalValida(EstadoInicial);
        if (estadoDeLaUrl != _estadoFiltro)
            _estadoFiltro = estadoDeLaUrl;

        var subcontrataDeLaUrl = IdDesdeUrl(SubcontrataInicial);
        var empresaDeLaUrl = subcontrataDeLaUrl.Length > 0 ? string.Empty : IdDesdeUrl(EmpresaInicial);
        if (subcontrataDeLaUrl != _filtroSubcontrataId)
            _filtroSubcontrataId = subcontrataDeLaUrl;
        if (empresaDeLaUrl != _filtroEmpresaId)
            _filtroEmpresaId = empresaDeLaUrl;
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

    private async ValueTask<GridItemsProviderResult<VehiculoListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<VehiculoListaDto> request)
    {
        if (_servirPaginaEnMemoria)
        {
            // Refresco de una fila tras guardar en la vista rápida (ver RefrescarFilaAsync).
            _servirPaginaEnMemoria = false;
            return GridItemsProviderResult.From(_elementosPagina.ToList(), _totalElementos);
        }

        if (_desechado)
            return GridItemsProviderResult.From(new List<VehiculoListaDto>(), 0);

        // Todo lo que define la pregunta —el número de carga y el token— se lee
        // ANTES del await. Leer _ciclo.Token después dejaría que un Dispose
        // intermedio lo hubiera desechado.
        var carga = ++_cargaVigente;

        // Dos motivos para cancelar: que la página se retire (el ciclo) y que
        // la rejilla pida otra página, otro orden u otro filtro (QuickGrid).
        using var cancelacion = CancellationTokenSource.CreateLinkedTokenSource(_ciclo.Token, request.CancellationToken);
        var token = cancelacion.Token;

        // La pregunta se compone entera ANTES del await, como el número de carga y el token.
        var (ordenarPor, descendente) = LecturaOrden.Leer(request);
        var consulta = new ObtenerVehiculosQuery(
            Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
            EmpresaId: Guid.TryParse(_filtroEmpresaId, out var empresaId) ? empresaId : null,
            SubcontrataId: Guid.TryParse(_filtroSubcontrataId, out var subcontrataId) ? subcontrataId : null,
            Pagina: (request.StartIndex / _paginacion.ItemsPerPage) + 1,
            TamanoPagina: _paginacion.ItemsPerPage,
            OrdenarPor: ordenarPor,
            Descendente: descendente,
            EstadoDocumental: string.IsNullOrWhiteSpace(_estadoFiltro) ? null : _estadoFiltro,
            ConRecuentosPorEstado: true,
            // Esta página pinta el motivo del estado: es quien pide el desglose.
            ConDesgloseDocumental: true);

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
            _ultimaColumnaOrden = request.SortByColumn;
            _ultimoOrdenAscendente = request.SortByAscending;

            var resultado = await Mediator.Send(consulta, token);

            // La respuesta de una búsqueda ya abandonada no puede pisar el
            // total, las filas ni la selección de la pregunta que sí se está
            // mirando. QuickGrid descarta por su cuenta el resultado de un
            // provider superado, pero el estado de la página lo escribimos aquí.
            if (!EsVigente(carga))
                return GridItemsProviderResult.From(new List<VehiculoListaDto>(), 0);

            _totalElementos = resultado.TotalElementos;
            _recuentosPorEstado = resultado.RecuentosPorEstado;

            var elementos = resultado.Elementos.ToList();
            _elementosPagina = elementos;
            if (conservarSeleccion)
            {
                // Lo que ya no está en la página (la corrección lo sacó del filtro) deja de estar seleccionado.
                _seleccionados.IntersectWith(elementos.Select(v => v.Id));
                if (_idEnfocado is { } enfocado && elementos.All(v => v.Id != enfocado))
                    _idEnfocado = null;
            }
            else
            {
                _seleccionados.Clear();
                _idEnfocado = null;
            }

            return GridItemsProviderResult.From(elementos, resultado.TotalElementos);
        }
        catch (Exception) when (!EsVigente(carga))
        {
            // Una carga superada que falla no es un error de la vigente: no
            // puede tapar su resultado con el estado de error de una pregunta
            // que ya nadie hace.
            return GridItemsProviderResult.From(new List<VehiculoListaDto>(), 0);
        }
        catch (Exception)
        {
            _errorCarga = true;
            return GridItemsProviderResult.From(new List<VehiculoListaDto>(), 0);
        }
        finally
        {
            if (EsVigente(carga))
            {
                _cargando = false;
                StateHasChanged();
            }
        }
    }

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

    /// <summary>
    /// Escoge el campo de la celda combinada sin modificar el botón de orden nativo. La ordenación
    /// se ejecuta después del render, cuando QuickGrid ya recibió el nuevo SortBy de la columna.
    /// </summary>
    private Task CambiarCampoOrdenVehiculoAsync(string valor)
    {
        if (valor is not (nameof(VehiculoListaDto.Nombre) or nameof(VehiculoListaDto.Modelo)) || valor == _campoOrdenVehiculo)
            return Task.CompletedTask;

        _campoOrdenVehiculo = valor;
        _direccionOrdenPendiente = ReferenceEquals(_ultimaColumnaOrden, _columnaVehiculo) && !_ultimoOrdenAscendente
            ? SortDirection.Descending : SortDirection.Ascending;
        _ordenVehiculoPendiente = true;
        return Task.CompletedTask;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var grid = _grid;
        var columna = _columnaVehiculo;
        if (_desechado || !_ordenVehiculoPendiente || grid is null || columna is null)
            return;

        // Consumir antes del await evita repetir el orden en el render que provoca la carga.
        _ordenVehiculoPendiente = false;
        await grid.SortByColumnAsync(columna, _direccionOrdenPendiente);
        if (!_desechado && ReferenceEquals(grid, _grid))
            await grid.HideColumnOptionsAsync();
    }

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
    /// QuickGrid vuelve a pedir la misma página por su cuenta en el render siguiente, y esa segunda petición es
    /// el mismo refresco, no una recarga nueva. Mismo patrón que <c>Trabajadores.razor.cs</c>.
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
        if (_desechado || _grid is null)
            return;

        _consultaQueConservaSeleccion = _ultimaConsulta;
        await _grid.RefreshDataAsync();
        StateHasChanged();
    }

    /// <summary>
    /// Vuelve a la página 1 y pide la lista UNA vez.
    /// <see cref="PaginationState.SetCurrentPageIndexAsync"/> no lleva guarda de
    /// igualdad: avisa a QuickGrid cambie o no la página, y QuickGrid recarga al
    /// recibir el aviso. Llamar además a <c>RefreshDataAsync</c> pedía dos veces
    /// lo mismo. Ver <c>Clientes.razor.cs</c> para el detalle del componente.
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
        _empresaId = string.Empty;
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
        // «fila-pulsable»: QuickGrid no ofrece clic de fila, así que es atajos-lista.js quien, al
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
                    await AbrirVistaRapidaAsync(idAbrir);
                break;
        }

        StateHasChanged();
    }
}
