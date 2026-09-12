using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCanalesGestionDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Domain.Centros;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Centros.Pages;

/// <summary>
/// Centro 360 (Centro 360 TALVEG, Parte XVI PROMPT 13) — la "segunda pantalla"
/// a la que se llega al pedir ver un centro por completo, ya sea desde el
/// enlace "Ver Centro 360" de la vista previa recortada de /centros
/// (AcordeonAsignacionesCentro.SoloIncidencias) o directamente por URL.
///
/// Deliberadamente NO reimplementa edición de Información, Requisitos del
/// Centro, Vehículos, Agenda ni Historial: eso ya vive, completo, en
/// CentroWorkspacePanel (el panel lateral que abre "Detalles" en /centros).
/// Esta página compone lo que ese panel no ofrece de un vistazo — cabecera
/// de cumplimiento, próxima visita y la lista de trabajadores COMPLETA (a
/// diferencia de la vista previa) — y remite al panel para operar.
/// </summary>
public partial class CentroDetalle : ComponentBase, IDisposable
{
    [Parameter] public Guid CentroId { get; set; }

    /// <summary>
    /// Filtros de la barra de trabajo, en la URL. Viajan ahí para que un
    /// centro filtrado se pueda enlazar y para que recargar no pierda lo que
    /// el gestor estaba mirando — igual que el resto de listas de la
    /// aplicación.
    /// </summary>
    [Parameter, SupplyParameterFromQuery(Name = "q")] public string? Busqueda { get; set; }

    [Parameter, SupplyParameterFromQuery(Name = "estado")] public string? Estado { get; set; }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ContextWorkspaceService WorkspaceService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private CentroDetalleDto? _detalle;
    private CentroListaDto? _resumen;
    private IReadOnlyList<VisitaResumenDto> _visitas = [];
    private IReadOnlyList<CanalGestionResumenDto> _canales = [];
    private int _documentosPorReclamar;
    private bool _cargando = true;
    private bool _error;
    private bool _cargandoCanales = true;

    /// <summary>
    /// Centro que esta instancia tiene cargado. La ruta es una sola
    /// (<c>/centros/{id}</c>): Blazor reutiliza esta instancia al pasar de un
    /// centro a otro, y sin esto cada cambio de filtro en la URL relanzaría la
    /// carga entera.
    /// </summary>
    private Guid? _centroCargado;

    /// <summary>
    /// Generación del centro que se enseña, y un contador por carga. Cambiar de
    /// centro y retirar la página los suben: cada respuesta compara el suyo
    /// antes de escribir y, si ya no es el vigente, se descarta. Abrir otro
    /// centro con una carga en vuelo no puede pintar el anterior.
    /// </summary>
    private int _generacion;
    private int _cargaDetalle;
    private int _cargaCanales;

    /// <summary>
    /// Los contadores impiden que una respuesta tardía escriba; esto corta la
    /// consulta misma. Todas las consultas de esta página llevan su token y
    /// <see cref="Dispose"/> lo cancela: al navegar fuera, lo que estaba en
    /// vuelo deja de ocupar el DbContext del circuito. El token se copia al
    /// inicializar porque leer <c>Token</c> de un
    /// <see cref="CancellationTokenSource"/> ya desechado lanza.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private CancellationToken _cancelacion;

    private string _busqueda = string.Empty;
    private string _estadoFiltro = string.Empty;
    private string? _busquedaDeLaUrl;
    private string? _estadoDeLaUrl;

    private IReadOnlyList<IncidenciaCentroDto> IncidenciasEmpresaHeader =>
        _resumen is null
            ? []
            : _resumen.Recuentos.Vencidas.Concat(_resumen.Recuentos.Proximas).Where(i => i.Ambito == AmbitoCausa.Empresa).ToList();

    private IReadOnlyList<BreadcrumbElemento> Miguero =>
        new[] { new BreadcrumbElemento("Centros"), new BreadcrumbElemento(_detalle?.Nombre ?? "…") };

    protected override void OnInitialized() => _cancelacion = _ciclo.Token;

    /// <summary>
    /// Solo recarga cuando cambia el centro. Los filtros de la barra de trabajo
    /// también llegan por aquí (viajan en la URL) y no tocan ninguna consulta:
    /// filtran client-side lo ya cargado.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        AdoptarFiltrosDeLaUrl();

        if (_centroCargado == CentroId)
            return;

        _centroCargado = CentroId;
        ReiniciarParaNuevoCentro();
        await CargarAsync();
    }

    /// <summary>
    /// Los filtros de la URL se adoptan cuando cambian AHÍ FUERA —un deep link,
    /// el botón «atrás»—, no cada vez que se repinta: releerlos siempre pisaría
    /// lo que se está tecleando, porque <c>CampoTexto</c> tarda 300 ms en
    /// avisar y la URL va por detrás de la tecla.
    /// </summary>
    private void AdoptarFiltrosDeLaUrl()
    {
        if (!string.Equals(_busquedaDeLaUrl, Busqueda, StringComparison.Ordinal))
        {
            _busquedaDeLaUrl = Busqueda;
            _busqueda = Busqueda ?? string.Empty;
        }

        if (!string.Equals(_estadoDeLaUrl, Estado, StringComparison.Ordinal))
        {
            _estadoDeLaUrl = Estado;
            _estadoFiltro = Estado ?? string.Empty;
        }
    }

    private void Buscar(string valor)
    {
        _busqueda = valor;
        _busquedaDeLaUrl = string.IsNullOrWhiteSpace(valor) ? null : valor;
        NavigationManager.ActualizarFiltroEnUrl("q", valor);
    }

    private void CambiarEstado(string valor)
    {
        _estadoFiltro = valor;
        _estadoDeLaUrl = string.IsNullOrWhiteSpace(valor) ? null : valor;
        NavigationManager.ActualizarFiltroEnUrl("estado", valor);
    }

    private bool HayFiltrosActivos => !string.IsNullOrWhiteSpace(_busqueda) || !string.IsNullOrWhiteSpace(_estadoFiltro);

    /// <summary>
    /// Quitar los filtros también los quita de la URL, en una sola navegación:
    /// una pantalla sin filtro cuya URL siga llevándolos los repone en cuanto
    /// alguien recarga o comparte el enlace.
    /// </summary>
    private void LimpiarFiltros()
    {
        _busqueda = string.Empty;
        _estadoFiltro = string.Empty;
        _busquedaDeLaUrl = null;
        _estadoDeLaUrl = null;
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?> { ["q"] = null, ["estado"] = null });
    }

    /// <summary>Invalida cualquier carga en vuelo: su respuesta ya no escribirá nada.</summary>
    private void InvalidarCargas()
    {
        _generacion++;
        _cargaDetalle++;
        _cargaCanales++;
    }

    /// <summary>
    /// Nada del centro anterior sobrevive al cambio: ni sus datos, ni su menú
    /// de acciones abierto, ni el recuento que decide si «Reclamar
    /// documentación» tiene objeto.
    /// </summary>
    private void ReiniciarParaNuevoCentro()
    {
        InvalidarCargas();
        _detalle = null;
        _resumen = null;
        _visitas = [];
        _canales = [];
        _documentosPorReclamar = 0;
        _error = false;
        _cargandoCanales = true;
    }

    private async Task CargarAsync()
    {
        var carga = ++_cargaDetalle;
        var centroId = CentroId;
        _cargando = true;
        _error = false;

        try
        {
            var detalle = await Mediator.Send(new ObtenerCentroPorIdQuery(centroId), _cancelacion);
            if (carga != _cargaDetalle) return;
            _detalle = detalle;
            if (_detalle is null)
            {
                _error = true;
                return;
            }

            var pagina = await Mediator.Send(new ObtenerCentrosQuery(
                Busqueda: null, ClienteId: null, Pagina: 1, TamanoPagina: 1, CentroId: centroId), _cancelacion);
            if (carga != _cargaDetalle) return;
            _resumen = pagina.Elementos.FirstOrDefault();

            var visitasPorCentro = await Mediator.Send(new ObtenerProximaVisitaPorCentroQuery([centroId]), _cancelacion);
            if (carga != _cargaDetalle) return;
            _visitas = visitasPorCentro.GetValueOrDefault(centroId) ?? [];

            // Cuántos documentos hay pendientes de reclamar en ESTE centro.
            // Decide si el botón "Reclamar documentación" se pinta y con qué
            // recuento: antes aparecía siempre, también cuando no había nada
            // que reclamar, y remitía al panel para que el gestor descubriera
            // allí que no había objeto. Mismo criterio de "no tumbar la
            // página" que CentroWorkspacePanel: si esto falla, el botón
            // simplemente no aparece — es contexto, no el Centro.
            try
            {
                var lotes = await Mediator.Send(new ObtenerLoteReclamacionQuery(CentroId: centroId), _cancelacion);
                if (carga != _cargaDetalle) return;
                _documentosPorReclamar = lotes.FirstOrDefault()?.Documentos.Count ?? 0;
            }
            catch (Exception)
            {
                if (carga == _cargaDetalle)
                    _documentosPorReclamar = 0;
            }

            // RendererInfo.IsInteractive: mismo motivo que TrabajadorDetalle
            // (ver ese commit) — OnParametersSetAsync también corre durante
            // el prerenderizado estático de una carga en frío, y un "_ = "
            // sin await ahí deja esta tarea en vuelo cuando ASP.NET Core ya
            // liberó el scope de DI al terminar esa fase, tirando el
            // DbContext a mitad de consulta (reportado en producción vía
            // Sentry: ObjectDisposedException sobre IServiceProvider,
            // mismo patrón exacto de pila que causó la carga en frío
            // colgada de Trabajador 360).
            if (RendererInfo.IsInteractive)
                _ = CargarCanalesAsync();
        }
        catch (Exception)
        {
            if (carga == _cargaDetalle)
                _error = true;
        }
        finally
        {
            if (carga == _cargaDetalle)
                _cargando = false;
        }
    }

    /// <summary>Aparte del resto: "Canales de gestión" es contexto de la barra lateral, no debe bloquear el render de la cabecera ni del cuerpo si tarda o falla.</summary>
    private async Task CargarCanalesAsync()
    {
        var carga = ++_cargaCanales;
        var centroId = CentroId;
        _cargandoCanales = true;
        try
        {
            var canales = await Mediator.Send(new ObtenerCanalesGestionDeCentroQuery(centroId), _cancelacion);
            if (carga != _cargaCanales) return;
            _canales = canales;
        }
        catch (Exception)
        {
            if (carga == _cargaCanales)
                _canales = [];
        }
        finally
        {
            if (carga == _cargaCanales)
            {
                _cargandoCanales = false;
                StateHasChanged();
            }
        }
    }

    /// <summary>
    /// Al navegar fuera se invalida lo que estuviera en vuelo y se cancela la
    /// consulta emitida. La cancelación llega como una excepción que el catch
    /// de cada carga ya descarta, porque su contador dejó de ser el vigente.
    /// Solo <see cref="IDisposable"/>: esta página no implementa
    /// <c>IAsyncDisposable</c>, así que este es el único que Blazor llama.
    /// </summary>
    public void Dispose()
    {
        InvalidarCargas();
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    private void IrABreadcrumb(int indice)
    {
        if (indice == 0)
            NavigationManager.NavigateTo("/centros");
    }

    private void AbrirInformacion() =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Centro, CentroId, _detalle?.Nombre ?? string.Empty, "informacion");

    private void AbrirPlataforma() =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Centro, CentroId, _detalle?.Nombre ?? string.Empty, "plataforma");

    private void AbrirClienteEmpresarial() =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Cliente, _detalle!.ClienteId, _detalle.ClienteRazonSocial, "informacion");

    private void AbrirEmpresa() =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Empresa, _detalle!.EmpresaId, _detalle.EmpresaRazonSocial, "informacion");

    private void ProgramarVisita() =>
        NavigationManager.NavigateTo($"/visitas?centroId={CentroId}");

    /// <summary>
    /// "Generar informe del centro" del mockup no describe un informe nuevo —
    /// /reportes ya genera Vigencia documental/Incidencias/Asignaciones con
    /// alcance por Centro (GenerarInformeVigenciaQuery/GenerarInformeAsignacionesQuery,
    /// ambas con CentroId). Es un deep-link que preselecciona cliente y centro,
    /// mismo patrón que "Desglosar por cliente" de Inicio.
    /// </summary>
    private void GenerarInformeDelCentro() =>
        NavigationManager.NavigateTo($"/reportes?clienteId={_detalle!.ClienteId}&centroId={CentroId}");

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

        return partes.Count == 0 ? cabeza : $"{cabeza} — {string.Join(", ", partes)}";
    }
}
