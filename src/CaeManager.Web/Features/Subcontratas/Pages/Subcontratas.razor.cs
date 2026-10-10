using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Subcontratas.Commands.CrearSubcontrata;
using CaeManager.Application.Subcontratas.Commands.EliminarSubcontratas;
using CaeManager.Application.Subcontratas.Commands.RestaurarSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Application.Common;
using CaeManager.Web.Components.Layout;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Features.Subcontratas.Recursos;
using CaeManager.Web.Recursos;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Subcontratas.Pages;

public partial class Subcontratas : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    /// <summary>Quien mira no alcanza nada en este Tenant (<see cref="CaeManager.Web.Features.IncorporacionCartera.Components.VacioSegunAlcance"/>):
    /// sin «+ Nuevo» en cabecera, para no duplicar lo que quizá ya existe fuera de su cartera.</summary>
    private bool _alcanceCero;

    // Igual que Centros.razor.cs (Centro 360): QuickGrid no soporta filas
    // expandibles, así que la paginación se gestiona a mano — la Query sigue
    // paginando en servidor, solo cambia el control visual.
    private const int TamanoPaginaMinimo = 20;
    private int _tamanoPagina = TamanoPaginaMinimo;

    private string _busqueda = string.Empty;

    /// <summary>Filtro «Nivel de servicio»: nombre del enum (Gestionada / Supervisada) o vacío. Viaja en la URL como <c>nivel</c>.</summary>
    private string _nivelFiltro = string.Empty;

    /// <summary>Selección de la franja de estado: estados de código separados por coma, tal como viajan en <c>?estado=</c>.</summary>
    private string _estadoFiltro = string.Empty;

    /// <summary>Subcontratas por estado documental con los demás filtros aplicados; <c>null</c> hasta la primera carga.</summary>
    private IReadOnlyDictionary<string, int>? _recuentosPorEstado;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;
    private int _pagina = 1;

    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_totalElementos / (double)_tamanoPagina));

    private IReadOnlyList<ClienteSelectorDto> _clientesDisponibles = [];
    private IReadOnlyList<EmpresaSelectorDto> _empresasDisponibles = [];
    private IReadOnlyList<ElementoSeleccionable> _clientesDisponiblesSelector => _clientesDisponibles
        .Select(c => new ElementoSeleccionable(c.Id, c.RazonSocial))
        .ToList();
    private IReadOnlyList<ElementoSeleccionable> _empresasDisponiblesSelector => _empresasDisponibles
        .Select(e => new ElementoSeleccionable(e.Id, e.RazonSocial))
        .ToList();

    private bool _drawerVisible;
    private string _razonSocial = string.Empty;
    private string _cif = string.Empty;
    private HashSet<Guid> _clienteIdsSeleccionados = [];
    private HashSet<Guid> _empresaIdsSeleccionados = [];

    // DDL-076: en perfil Cliente Directo con una única Empresa, el selector
    // de Empresas no aparece — se marca en silencio. Mismo mecanismo que
    // Trabajadores.razor.cs, adaptado a la relación N:N de Subcontrata con
    // Empresa (aquí no hay "tipo de empleador" que alternar: una Subcontrata
    // siempre se relaciona con Empresas, nunca con una sola de forma exclusiva).
    private bool _resolverEmpresaEnSilencio;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private readonly HashSet<Guid> _seleccionados = [];
    private bool _seleccionMultiple;

    private void AlternarSeleccionMultiple(bool activa)
    {
        _seleccionMultiple = activa;
        if (!activa)
            _seleccionados.Clear();
    }

    /// <summary>Qué filas tienen el acordeón abierto — mismo criterio que Centros.razor.cs.</summary>
    private readonly HashSet<Guid> _expandidos = [];
    private List<SubcontrataListaDto> _elementosPagina = [];
    private Guid? _idEnfocado;
    private bool _eliminandoLote;
    private bool _confirmarEliminarLoteVisible;

    /// <summary>Subcontrata cuyo panel está abierto arriba de la pila del Context Workspace, si la hay.</summary>
    private Guid? SubcontrataEnVistaPrevia =>
        WorkspaceService.FrameActual is { Tipo: EntidadWorkspace.Subcontrata } frame ? frame.EntidadId : null;

    private void AlCambiarWorkspace() => InvokeAsync(StateHasChanged);

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    [SupplyParameterFromQuery(Name = "nivel")]
    public string? NivelInicial { get; set; }

    [SupplyParameterFromQuery(Name = "estado")]
    public string? EstadoInicial { get; set; }

    /// <summary>Comando del palette "Crear subcontrata": /subcontratas?accion=crear abre el modal directamente — mismo patrón que Clientes/Empresas/Centros/Trabajadores/Documentos.</summary>
    [SupplyParameterFromQuery] public string? Accion { get; set; }

    /// <summary>Estado 4a del mockup del selector: hay que elegir una empresa de la cartera antes de ver la lista.</summary>
    private bool _sinEmpresaSeleccionada;

    /// <summary>La empresa activa aún no se ha resuelto: se pinta una carga en vez de la lista.</summary>
    private bool _resolviendoEmpresa = true;

    [Inject] private ITenantActual TenantActual { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private IValidator<CrearSubcontrataCommand> ValidadorCrear { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosSubcontratas> Textos { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosComunes> Comunes { get; set; } = default!;

    /// <summary>Se cancela al retirar la página: la resolución de la empresa activa no sigue consultando servicios del circuito.</summary>
    private readonly CancellationTokenSource _ciclo = new();

    // ── La fila se refresca tras guardar en la vista rápida ─────────────────────────────────────
    // El panel vive en MainLayout y guarda sin pasar por esta página: avisa por
    // ContextWorkspaceService.OnEntidadGuardada. Se vuelve a pedir SOLO esa fila y se sustituye
    // en sitio (mismo criterio que Centros.RefrescarCentroAsync): filtros, orden, página,
    // selección, acordeones, fila enfocada y desplazamiento no se tocan, y la fila permanece
    // aunque el cambio la saque del filtro activo, hasta la siguiente carga. La vista rápida edita
    // datos de la Subcontrata, no documentos: el estado documental de la fila no cambia y la franja
    // no se vuelve a pedir. Hueco conocido: si el guardado cambia el nivel de servicio o la razón
    // social y hay un filtro de nivel o una búsqueda activos, la franja sigue contando esa fila
    // hasta la siguiente carga, igual que la fila sigue a la vista.
    private void AlGuardarEntidad(EntidadWorkspace tipo, Guid id)
    {
        if (tipo == EntidadWorkspace.Subcontrata)
            _ = InvokeAsync(() => RefrescarFilaAsync(id));
    }

    private async Task RefrescarFilaAsync(Guid id)
    {
        // Con una carga en vuelo no se sustituye nada: la sustitución caería sobre una página que
        // está a punto de cambiar. Hueco conocido: si esa carga leyó antes de que el guardado
        // fuera firme, la fila conserva el dato anterior hasta la siguiente carga.
        if (_cargando || !_elementosPagina.Any(s => s.Id == id))
            return;

        try
        {
            await RefrescarSubcontrataAsync(id, recontarFranja: false);
        }
        catch (Exception)
        {
            // El guardado ya es firme: que falle la relectura no es un error que enseñar. La
            // fila conserva el dato anterior hasta la siguiente carga, como antes de este aviso.
        }
    }

    public void Dispose()
    {
        WorkspaceService.OnCambio -= AlCambiarWorkspace;
        WorkspaceService.OnEntidadGuardada -= AlGuardarEntidad;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    protected override async Task OnInitializedAsync()
    {
        WorkspaceService.OnCambio += AlCambiarWorkspace;
        WorkspaceService.OnEntidadGuardada += AlGuardarEntidad;
        _busqueda = TerminoBusquedaInicial ?? string.Empty;
        _nivelFiltro = NivelDesdeUrl();
        _estadoFiltro = EstadoDesdeUrl();

        // Hasta resolver la empresa activa no se monta la lista ni sus acciones: con la consulta en
        // vuelo el render saldría con «hay empresa» y lanzaría la carga del Tenant de origen.
        try
        {
            var contexto = await ContextoEmpresaActiva.ResolverAsync(Mediator, TenantActual, _ciclo.Token);
            _sinEmpresaSeleccionada = contexto.SinSeleccion;
        }
        finally
        {
            _resolviendoEmpresa = false;
        }

        // La página se retiró mientras se resolvía la empresa: nada más que pedir.
        if (_ciclo.IsCancellationRequested)
            return;

        // Sin empresa elegida no se piden los datos de la organización de origen.
        if (_sinEmpresaSeleccionada)
            return;

        _puedeEscribir = await SoloConEscritura.PuedeEscribirAsync(EstadoAutenticacion);
        await CargarAsync();

        if (Accion == "crear")
            await AbrirCrear();
    }

    /// <summary>Se re-ejecuta en cada navegación dentro de la propia página — mismo criterio que Centros.razor.cs.</summary>
    protected override async Task OnParametersSetAsync()
    {
        if (_resolviendoEmpresa || _sinEmpresaSeleccionada)
            return;

        var deLaUrl = TerminoBusquedaInicial ?? string.Empty;
        var nivelDeLaUrl = NivelDesdeUrl();
        var estadoDeLaUrl = EstadoDesdeUrl();
        if (deLaUrl == _busqueda && nivelDeLaUrl == _nivelFiltro && estadoDeLaUrl == _estadoFiltro)
            return;

        _busqueda = deLaUrl;
        _nivelFiltro = nivelDeLaUrl;
        _estadoFiltro = estadoDeLaUrl;
        await CargarAsync(resetPagina: true);
    }

    /// <summary>
    /// El nivel de la URL solo se acepta si es uno del enum: la coordenada viene de fuera y no
    /// es autoridad sobre lo que existe. Uno desconocido se ignora (sin filtro), en vez de
    /// filtrar por algo que ninguna subcontrata puede tener.
    /// </summary>
    private string NivelDesdeUrl() => NivelValido(NivelInicial);

    private static string NivelValido(string? nivel) =>
        Enum.GetNames<NivelServicioSubcontrata>().Contains(nivel) ? nivel! : string.Empty;

    /// <summary>Mismo criterio para el estado: solo los estados que la franja ofrece.</summary>
    private string EstadoDesdeUrl() => EstadoDocumentoUi.SeleccionDocumentalValida(EstadoInicial);

    private NivelServicioSubcontrata? NivelSeleccionado =>
        Enum.TryParse<NivelServicioSubcontrata>(_nivelFiltro, out var nivel) ? nivel : null;

    /// <summary>La consulta de la página que se está viendo: filtros y paginación actuales.</summary>
    private ObtenerSubcontratasQuery ConsultaDePaginaActual() => new(
        Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
        Pagina: _pagina,
        TamanoPagina: _tamanoPagina,
        NivelServicio: NivelSeleccionado,
        EstadoDocumental: string.IsNullOrWhiteSpace(_estadoFiltro) ? null : _estadoFiltro,
        ConRecuentosPorEstado: true);

    [CascadingParameter] private Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState>? EstadoAutenticacion { get; set; }

    /// <summary>
    /// El rol efectivo puede escribir (misma pregunta que <see cref="SoloConEscritura"/>). Decide si
    /// las incidencias de las ventanas del motivo de la fila se ofrecen como pulsables.
    /// </summary>
    private bool _puedeEscribir;

    private CaeManager.Web.Features.Documentos.Components.CorreccionIncidenciaDocumental _correccion = default!;

    // Toda incidencia de una Subcontrata es de un Trabajador (ver IncidenciaSubcontrataDto): no hay Empresa que pasar.
    private bool EsCorregible(IncidenciaSubcontrataDto incidencia) =>
        _puedeEscribir && CaeManager.Web.Features.Documentos.Components.CorreccionIncidenciaDocumental.EsCorregible(
            incidencia.DocumentoId, incidencia.TipoDocumentoId, incidencia.TrabajadorId, empresaId: null);

    private bool HayCorregibles(IReadOnlyList<IncidenciaSubcontrataDto> incidencias) => incidencias.Any(EsCorregible);

    private string? PieDeIncidencias(IReadOnlyList<IncidenciaSubcontrataDto> incidencias) =>
        HayCorregibles(incidencias) ? Comunes["VentanaIncidenciasPie"].Value : null;

    private Task CorregirIncidenciaAsync(IncidenciaSubcontrataDto incidencia) =>
        _correccion.AbrirAsync(incidencia.DocumentoId, incidencia.TipoDocumentoId, incidencia.TrabajadorId, empresaId: null);

    /// <summary>
    /// Tras corregir una incidencia desde la ventana de contexto: el documento corregido puede
    /// contar en más de una fila, así que se vuelve a pedir la página tal como está y se sustituye
    /// en sitio, conservando la selección. Los acordeones se cierran porque su contenido ya no es
    /// el de antes de corregir (mismo criterio que Centros).
    /// </summary>
    private async Task RefrescarTrasCorreccionAsync()
    {
        ResultadoPaginado<SubcontrataListaDto> resultado;
        try
        {
            resultado = await Mediator.Send(ConsultaDePaginaActual());

            // Con el filtro de estado activo, corregir puede sacar del filtro la última fila de la
            // última página: la página pedida queda vacía aunque siga habiendo coincidencias. Se
            // retrocede a la última página que existe en vez de enseñar «ninguna coincide».
            if (resultado.Elementos.Count == 0 && resultado.TotalElementos > 0 && _pagina > 1)
            {
                _pagina = Math.Max(1, (int)Math.Ceiling(resultado.TotalElementos / (double)_tamanoPagina));
                resultado = await Mediator.Send(ConsultaDePaginaActual());
            }
        }
        catch (Exception)
        {
            // El documento ya se guardó: que falle la relectura no es un error del formulario.
            // CargarAsync enseña el estado de error de la lista, con su reintento.
            await CargarAsync();
            return;
        }

        _totalElementos = resultado.TotalElementos;
        _elementosPagina = resultado.Elementos.ToList();
        _recuentosPorEstado = resultado.RecuentosPorEstado;
        _seleccionados.IntersectWith(_elementosPagina.Select(s => s.Id));
        _expandidos.Clear();
        StateHasChanged();
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
            var resultado = await Mediator.Send(ConsultaDePaginaActual());

            _totalElementos = resultado.TotalElementos;
            _elementosPagina = resultado.Elementos.ToList();
            _recuentosPorEstado = resultado.RecuentosPorEstado;
            _seleccionados.Clear();
            _expandidos.Clear();
            _idEnfocado = null;
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

    private Task CambiarTamanoPaginaAsync(int tamano)
    {
        _tamanoPagina = tamano;
        return CargarAsync(resetPagina: true);
    }

    /// <summary>
    /// Tras gestionar un documento in situ desde el acordeón hace falta
    /// refrescar el cumplimiento/recuentos de ESA fila — CargarAsync()
    /// completo colapsaría el acordeón que el usuario acaba de usar (mismo
    /// motivo que RefrescarCentroAsync en Centros.razor.cs).
    /// </summary>
    private async Task RefrescarSubcontrataAsync(Guid subcontrataId, bool recontarFranja = true)
    {
        // Gestionar un documento puede cambiar el estado documental de la fila, y los recuentos de la
        // franja dependen de todas las filas, no solo de esta: se piden con la consulta de la página y
        // se sustituye en sitio la fila gestionada. Si con el cambio la fila ya no pasa el filtro de
        // estado, sigue a la vista con sus datos nuevos hasta la próxima carga: quitarla cerraría el
        // acordeón que se está usando. Sin recuento (guardado de la vista rápida) basta la fila por Id.
        var resultado = recontarFranja ? await Mediator.Send(ConsultaDePaginaActual()) : null;
        var actualizada = resultado?.Elementos.FirstOrDefault(s => s.Id == subcontrataId)
            ?? (await Mediator.Send(new ObtenerSubcontratasQuery(Busqueda: null, SubcontrataId: subcontrataId))).Elementos.FirstOrDefault();
        if (actualizada is null) return;

        if (resultado is not null)
            _recuentosPorEstado = resultado.RecuentosPorEstado;
        var indice = _elementosPagina.FindIndex(s => s.Id == subcontrataId);
        if (indice >= 0)
            _elementosPagina[indice] = actualizada;

        StateHasChanged();
    }

    private async Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        NavigationManager.ActualizarFiltroEnUrl("q", valor);
        await CargarAsync(resetPagina: true);
    }

    private async Task CambiarNivelAsync(string valor)
    {
        _nivelFiltro = valor;
        NavigationManager.ActualizarFiltroEnUrl("nivel", valor);
        await CargarAsync(resetPagina: true);
    }

    private async Task CambiarEstadoAsync(string? valor)
    {
        _estadoFiltro = valor ?? string.Empty;
        NavigationManager.ActualizarFiltroEnUrl("estado", valor);
        await CargarAsync(resetPagina: true);
    }

    private bool HayFiltrosActivos =>
        !string.IsNullOrWhiteSpace(_busqueda) || !string.IsNullOrWhiteSpace(_nivelFiltro) || !string.IsNullOrWhiteSpace(_estadoFiltro);

    private IReadOnlyList<OpcionEstado> OpcionesNivel =>
    [
        new(nameof(NivelServicioSubcontrata.Gestionada), EstadoSupervisionUi.TextoNivel(Textos, NivelServicioSubcontrata.Gestionada)),
        new(nameof(NivelServicioSubcontrata.Supervisada), EstadoSupervisionUi.TextoNivel(Textos, NivelServicioSubcontrata.Supervisada)),
    ];

    private string TextoNivelFiltro => OpcionesNivel.FirstOrDefault(o => o.Valor == _nivelFiltro)?.Texto ?? _nivelFiltro;

    private bool MostrarPaginador =>
        _totalElementos > TamanoPaginaMinimo || (_totalElementos > 0 && _tamanoPagina > TamanoPaginaMinimo);

    /// <summary>
    /// Limpia la búsqueda, el nivel y el estado en memoria Y en la URL, con UNA navegación y UNA
    /// consulta: los filtros vuelven por <see cref="OnParametersSetAsync"/> desde <c>?q=</c>,
    /// <c>?nivel=</c> y <c>?estado=</c>, así que limpiar solo los campos dejaría que la siguiente
    /// pasada de parámetros los repusiera, y escribir la URL por pasos dejaría entre ellos una URL
    /// con alguno todavía puesto.
    /// </summary>
    private async Task LimpiarFiltrosAsync()
    {
        _busqueda = string.Empty;
        _nivelFiltro = string.Empty;
        _estadoFiltro = string.Empty;
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?> { ["q"] = null, ["nivel"] = null, ["estado"] = null });
        await CargarAsync(resetPagina: true);
    }

    // ---- Filtros guardados (pieza compartida FiltrosGuardadosDeListado) ----

    private const string PantallaDeFiltrosGuardados =
        CaeManager.Application.Configuracion.Commands.GuardarFiltro.PantallasConFiltrosGuardados.Subcontratas;

    /// <summary>
    /// Lista blanca de los parámetros de VISTA de la URL: lo que guarda y aplica un filtro guardado.
    /// Fuera queda <c>accion</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> ParametrosDeVista = ["q", "nivel"];

    private readonly ConexionFiltrosGuardados _filtrosGuardados = new();

    /// <summary>
    /// Un filtro guardado define la vista entera: lo que no trae se quita. El nivel pasa por la misma
    /// validación que el de la URL (uno que ya no existe se ignora), y la URL se escribe en una sola
    /// navegación antes de recargar; así <see cref="OnParametersSetAsync"/> la encuentra igual que los campos.
    /// </summary>
    private async Task AplicarVistaGuardadaAsync(IReadOnlyDictionary<string, string?> vista)
    {
        _busqueda = vista.GetValueOrDefault("q") ?? string.Empty;
        _nivelFiltro = NivelValido(vista.GetValueOrDefault("nivel"));
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?> { ["q"] = _busqueda, ["nivel"] = _nivelFiltro });
        await CargarAsync(resetPagina: true);
    }

    /// <summary>
    /// «N de M» de la barra de herramientas. M es el total que devuelve la
    /// consulta, que con búsqueda ya viene filtrado — por eso la frase lo dice,
    /// y no promete cuántas hay sin filtro, que esta pantalla no sabe.
    /// </summary>
    private string TextoConteo =>
        (_totalElementos == 1, HayFiltrosActivos) switch
        {
            (true, false) => Textos["ConteoUno", _elementosPagina.Count, _totalElementos],
            (false, false) => Textos["ConteoVarios", _elementosPagina.Count, _totalElementos],
            (true, true) => Textos["ConteoUnoConBusqueda", _elementosPagina.Count, _totalElementos],
            (false, true) => Textos["ConteoVariosConBusqueda", _elementosPagina.Count, _totalElementos],
        };

    private string ClaseRejilla(string claseBase) =>
        $"{claseBase} rejilla-subcontratas" + (_seleccionMultiple ? " rejilla-subcontratas-seleccion" : string.Empty);

    private string ClaseTarjeta(SubcontrataListaDto subcontrata) =>
        "tarjeta-fila-acordeon"
        + (subcontrata.Id == _idEnfocado ? " fila-enfocada" : string.Empty)
        + (subcontrata.Id == SubcontrataEnVistaPrevia ? " fila-en-vista-previa" : string.Empty)
        + ClaseTinte(subcontrata.Recuentos);

    /// <summary>
    /// Fila con problema (rediseño de listados, fase 1): algún documento vencido → tinte de
    /// peligro; si no, alguno urgente → de aviso; el resto, sin tinte.
    /// </summary>
    private static string ClaseTinte(RecuentosSubcontrataDto recuentos) =>
        recuentos.TotalVencidas > 0 ? " fila-tintada-peligro"
        : recuentos.Proximas.Any(p => p.Estado == EstadoDocumento.Urgente) ? " fila-tintada-aviso"
        : string.Empty;

    // Un clic en la fila, su nombre o Enter sobre la fila enfocada abren la vista rápida: el
    // panel de 520 px del Context Workspace, el mismo que abren los botones 360 del resto de
    // pantallas. A la página Subcontrata 360 (/subcontratas/{id}) se va con el icono 360 de
    // la fila.
    private Task AbrirVistaRapidaAsync(Guid id) =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Subcontrata, id, NombreDe(id), "informacion");

    /// <summary>
    /// Tecla «e»: la vista rápida de la fila enfocada, ya en edición (el lápiz de la cabecera
    /// del panel). Si el rol no puede escribir, el panel se abre y se queda en lectura.
    /// </summary>
    private Task AbrirVistaRapidaEnEdicionAsync(Guid id) =>
        WorkspaceService.AbrirEnEdicionAsync(EntidadWorkspace.Subcontrata, id, NombreDe(id));

    // La Subcontrata del panel puede no estar en la página (el filtro la dejó fuera): su
    // nombre es entonces el del frame abierto.
    private string NombreDe(Guid id) =>
        _elementosPagina.FirstOrDefault(e => e.Id == id)?.RazonSocial
        ?? (WorkspaceService.FrameActual is { } frame && frame.EntidadId == id ? frame.TituloVisible : string.Empty);

    private async Task AbrirCrear()
    {
        _clientesDisponibles = await Mediator.Send(new ObtenerClientesParaSelectorQuery());
        _empresasDisponibles = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery());

        var perfil = await Mediator.Send(new ObtenerPerfilVocabularioActualQuery());
        _resolverEmpresaEnSilencio = perfil == PerfilVocabularioTenant.ClienteDirecto && _empresasDisponibles.Count == 1;

        _razonSocial = string.Empty;
        _cif = string.Empty;
        _clienteIdsSeleccionados = [];
        _empresaIdsSeleccionados = _resolverEmpresaEnSilencio ? [_empresasDisponibles[0].Id] : [];
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
        FijarInstantaneaFormulario();
    }

    private readonly InstantaneaFormulario _instantanea = new();

    /// <summary>
    /// P1-E2b: único punto de verdad de «hay cambios» en el drawer de alta de Subcontrata. Lo lee
    /// DrawerFormulario, que lleva dentro el guardián de cerrar y de salir de la página; cerrado
    /// (también tras guardar) nunca hay nada que perder.
    /// </summary>
    private bool HayCambiosSinGuardar => _drawerVisible && _instantanea.Difiere(ValoresFormulario());

    private object?[] ValoresFormulario() => [_razonSocial, _cif, _clienteIdsSeleccionados, _empresaIdsSeleccionados];

    private void FijarInstantaneaFormulario() => _instantanea.Fijar(ValoresFormulario());

    private void AlternarCliente(Guid clienteId, bool seleccionado)
    {
        if (seleccionado)
            _clienteIdsSeleccionados.Add(clienteId);
        else
            _clienteIdsSeleccionados.Remove(clienteId);
    }

    private void AlternarEmpresa(Guid empresaId, bool seleccionado)
    {
        if (seleccionado)
            _empresaIdsSeleccionados.Add(empresaId);
        else
            _empresaIdsSeleccionados.Remove(empresaId);
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
            var clienteIds = _clienteIdsSeleccionados.ToList();
            var empresaIds = _empresaIdsSeleccionados.ToList();
            var cif = string.IsNullOrWhiteSpace(_cif) ? null : _cif;

            var resultado = await Mediator.Send(new CrearSubcontrataCommand(_razonSocial, cif, clienteIds, empresaIds));
            if (resultado.EsFallido)
            {
                _mensajeErrorFormulario = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar(Textos["ToastCreada"], TonoToast.Exito);
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
            _mensajeErrorFormulario = Textos["ErrorGuardarCambios"];
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
    private Task ValidarRazonSocialAsync() => ValidarCampoAsync(nameof(CrearSubcontrataCommand.RazonSocial));

    private Task ValidarCifAsync() => ValidarCampoAsync(nameof(CrearSubcontrataCommand.Cif));

    private async Task ValidarCampoAsync(string campo)
    {
        var cif = string.IsNullOrWhiteSpace(_cif) ? null : _cif;
        var resultado = await ValidadorCrear.ValidateAsync(
            new CrearSubcontrataCommand(_razonSocial, cif, _clienteIdsSeleccionados.ToList(), _empresaIdsSeleccionados.ToList()),
            opciones => opciones.IncludeProperties(campo));

        if (resultado.IsValid)
            _erroresCampo.Remove(campo);
        else
            _erroresCampo[campo] = resultado.Errors[0].ErrorMessage;
    }

    private bool TodosSeleccionados =>
        _elementosPagina.Count > 0 && _elementosPagina.All(e => _seleccionados.Contains(e.Id));

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

    private bool _restaurandoLote;

    /// <summary>«Deshacer» del aviso de una eliminación en lote: un único deshacer restaura todos los que el lote sí eliminó.</summary>
    private async Task DeshacerEliminarLoteAsync(IReadOnlyList<Guid> ids)
    {
        if (_restaurandoLote) return;
        _restaurandoLote = true;

        try
        {
            var r = await RestauracionEnLote.RestaurarAsync(ids, id => Mediator.Send(new RestaurarSubcontrataCommand(id)));

            ToastService.Mostrar(
                r.Errores.Count == 0 ? Textos["ToastLoteRestauradas", r.Restaurados].Value : Textos["ToastLoteRestauradasConErrores", r.Restaurados, r.Errores.Count, string.Join(" ", r.Errores)].Value,
                r.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);

            if (r.Restaurados > 0)
                await CargarAsync();
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
        _eliminandoLote = true;

        try
        {
            var idsPedidos = _seleccionados.ToList();
            var resultado = await Mediator.Send(new EliminarSubcontratasCommand(idsPedidos));
            var dto = resultado.Valor;
            IReadOnlyList<Guid> eliminadas = dto.IdsEliminados ?? [];

            ToastService.Mostrar(
                dto.Errores.Count == 0
                    ? Textos["ToastLoteEliminadas", dto.Eliminados]
                    : Textos["ToastLoteEliminadasConErrores", dto.Eliminados, dto.Errores.Count, string.Join(" ", dto.Errores)],
                dto.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia,
                eliminadas.Count > 0 ? Textos["ToastAccionDeshacer"].Value : null,
                eliminadas.Count > 0 ? () => DeshacerEliminarLoteAsync(eliminadas) : null);

            // Se retiran solo las fichas de los que cayeron (IdsEliminados); un superviviente
            // conserva la suya y su edición sin guardar.
            if (dto.Eliminados > 0)
                WorkspaceService.RetirarSiEstaAbierto(EntidadWorkspace.Subcontrata, dto.IdsEliminados ?? idsPedidos);

            _seleccionados.Clear();
            _confirmarEliminarLoteVisible = false;
            await CargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["ToastErrorEliminarLote"], TonoToast.Error);
        }
        finally
        {
            _eliminandoLote = false;
        }
    }

    private async Task ManejarAtajoAsync(string tecla)
    {
        // «e» no depende de que haya filas: sin fila enfocada edita la ficha que esté abierta,
        // aunque el filtro haya dejado la lista vacía.
        if (tecla == "e")
        {
            // La fila enfocada siempre está en la página: cada recarga de la lista la olvida.
            if ((_idEnfocado ?? SubcontrataEnVistaPrevia) is { } idEditar)
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
                // Marcar enciende la selección múltiple: una fila marcada sin casilla a la
                // vista sería selección invisible justo antes de «Eliminar seleccionados».
                if (_idEnfocado is { } idAlternar)
                {
                    _seleccionMultiple = true;
                    AlternarSeleccion(idAlternar, !_seleccionados.Contains(idAlternar));
                }
                break;
            case "Enter":
                // Enter hace lo mismo que un clic en la fila: abrir la vista rápida.
                if (_idEnfocado is { } idAbrir)
                    await AbrirVistaRapidaAsync(idAbrir);
                break;
        }

        StateHasChanged();
    }

    /// <summary>
    /// Recuento con su plural: la clave <paramref name="claveUno"/> para 1 y
    /// <paramref name="claveVarios"/> (con el número en {0}) para el resto. Sirve
    /// al nombre accesible y al título de la ventana de contexto —mismo criterio
    /// que Centros.razor.cs.DescribirRecuento— y al texto visible del motivo
    /// («1 vencido», «3 por vencer»).
    /// </summary>
    private string DescribirRecuento(int total, string claveUno, string claveVarios) =>
        total == 1 ? Textos[claveUno] : Textos[claveVarios, total];

    // Las cifras del motivo, bajo la pastilla de estado: lo que antes decían las columnas «Vencidos» y
    // «Por vencer», más lo que está sin confirmar.
    private string MotivoVencidos(int total) => DescribirRecuento(total, "BadgeVencidosUno", "BadgeVencidosVarios");

    private string MotivoProximos(int total) => DescribirRecuento(total, "BadgeProximosUno", "BadgeProximosVarios");

    private string MotivoSinConfirmar(int total) => DescribirRecuento(total, "MotivoSinConfirmarUno", "MotivoSinConfirmarVarios");

    /// <summary>
    /// Nombre accesible del disparador de una cifra del motivo. Empieza por el texto que se ve («2 vencidos»)
    /// para que quien lo nombre de viva voz lo active (WCAG 2.5.3, el nombre contiene la etiqueta visible), y
    /// sigue con la frase completa.
    /// </summary>
    private static string EtiquetaDeMotivo(string textoVisible, string fraseCompleta) => $"{textoVisible}. {fraseCompleta}";

    /// <summary>
    /// Nombre accesible de la ventana de Próximas. El badge visual ya
    /// distingue Urgente de Próximo por incidencia (Codex, oleada 3); sin
    /// este aviso en el nombre accesible, quien usa lector de pantalla no
    /// recibe esa misma distinción de severidad — solo el recuento genérico.
    /// </summary>
    private string EtiquetaProximos(IReadOnlyList<IncidenciaSubcontrataDto> proximas)
    {
        var etiquetaBase = DescribirRecuento(proximas.Count, "RecuentoDocumentosProximosUno", "AriaRecuentoDocumentosProximosVarios");
        var totalUrgentes = proximas.Count(i => i.Estado == EstadoDocumento.Urgente);
        return totalUrgentes switch
        {
            0 => etiquetaBase,
            1 => $"{etiquetaBase}, {Textos["AriaAvisoUnUrgenteEnProximos"]}",
            _ => $"{etiquetaBase}, {Textos["AriaAvisoVariosUrgentesEnProximos", totalUrgentes]}",
        };
    }

    /// <summary>
    /// Nombre accesible de la barra de cumplimiento. Antes se interpolaba el porcentaje sin
    /// mirar si existía, y una subcontrata sin universo de requisitos se
    /// anunciaba como «% de cumplimiento…» — un número que no hay. Null
    /// significa que ningún trabajador tiene un documento exigido por un
    /// centro activo (ver <see cref="SubcontrataListaDto"/>).
    /// </summary>
    private string EtiquetaCumplimiento(int? porcentaje) =>
        porcentaje is { } p
            ? Textos["EtiquetaCumplimiento", p]
            : Textos["EtiquetaCumplimientoSinUniverso"];

    /// <summary>
    /// Qué significa cada nivel de servicio, con las mismas palabras que el
    /// panel de Subcontrata 360. El mockup decía «TALVEG gestiona su
    /// documentación», y eso atribuye a la plataforma el papel de Operador
    /// CAE: quien gestiona es la organización que opera el tenant, no TALVEG.
    /// </summary>
    private string DescribirNivel(NivelServicioSubcontrata nivel) => nivel switch
    {
        NivelServicioSubcontrata.Supervisada => Textos["DescripcionNivelSupervisadaLista"],
        _ => Textos["DescripcionNivelGestionadaLista"]
    };

    /// <summary>
    /// Los criterios de «Exportar esta vista»: los mismos que esta página pasa a la consulta del
    /// listado, con los nombres de parámetro del endpoint de exportación, que los lee igual.
    /// </summary>
    private Dictionary<string, string?> CriteriosExportar => new()
    {
        ["q"] = _busqueda,
        ["nivel"] = NivelSeleccionado?.ToString(),
        ["estado"] = string.IsNullOrWhiteSpace(_estadoFiltro) ? null : _estadoFiltro,
    };
}
