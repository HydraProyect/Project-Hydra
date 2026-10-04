using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Recursos;
using CaeManager.Web.Services;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace CaeManager.Web.Components.Layout;

/// <summary>
/// Cambiar de cliente activo hace POST (con token antiforgery) al endpoint
/// <c>/cuenta/cliente-activo</c>, que escribe la cookie de workspace y
/// redirige — nunca se cambia en vivo dentro del circuito de Blazor Server.
/// Ver <c>ClienteActivoSeleccionado</c> (Web) para el motivo completo: un
/// cambio puramente en memoria no sobrevive a nada que necesite refrescar la
/// página actual, y un reload sin ese endpoint perdería la elección antes de
/// que ninguna página llegara a leerla.
///
/// Antes era una navegación GET disparada por <c>@onchange</c>. Se cambió a
/// POST por el hallazgo M-8 (cambio de estado sin antiforgery); el efecto
/// secundario es que el selector ya no necesita circuito interactivo para
/// funcionar, porque el envío lo hace el propio navegador.
/// </summary>
public partial class SelectorClienteActivo
{
    /// <summary>
    /// Desde cuántos Tenants la lista lleva buscador (contrato del selector,
    /// decisión 7 ter): por debajo, el buscador es ruido.
    /// </summary>
    public const int UmbralBusqueda = 15;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IClienteActivoSeleccionado ClienteActivoSeleccionado { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private AntiforgeryStateProvider AntiforgeryStateProvider { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosComunes> Textos { get; set; } = default!;
    [Inject] private ILogger<ExcepcionDeCircuitoDesconectado> Logger { get; set; } = default!;
    [Inject] private ICurrentUserService CurrentUserService { get; set; } = default!;
    [Inject] private IContadorPendientesSelectorTenant ContadorPendientes { get; set; } = default!;
    [Inject] private ILectorRecientesSelectorTenant LectorRecientes { get; set; } = default!;

    private IReadOnlyList<ClienteAutorizadoDto>? _clientes;
    private ClienteAutorizadoDto? _activo;
    private ClienteAutorizadoDto? _origen;
    private ElementReference _buscador;
    private bool _enfocarBuscador;
    private int _totalCartera;
    private AntiforgeryRequestToken? _token;
    private bool _abierto;
    private string _busqueda = string.Empty;
    private IReadOnlyList<Guid> _recientesIds = [];
    private IReadOnlyDictionary<Guid, int>? _pendientes;
    private Guid? _usuarioId;
    private bool _pendientesIniciados;
    private bool _desechado;

    /// <summary>Tope de espera del contador: pasado, el selector sigue sin contador (nunca bloquea).</summary>
    public static readonly TimeSpan EsperaMaximaPendientes = TimeSpan.FromSeconds(15);

    private string Busqueda
    {
        get => _busqueda;
        set => _busqueda = value ?? string.Empty;
    }

    /// <summary>
    /// La lista tal como se pinta: filtrada por el buscador sin distinguir
    /// mayúsculas ni acentos. El buscador solo existe con
    /// <see cref="UmbralBusqueda"/> Tenants o más; por debajo, su texto es siempre vacío.
    /// </summary>
    private IEnumerable<ClienteAutorizadoDto> Filtrados => string.IsNullOrWhiteSpace(_busqueda)
        ? _clientes!.Where(c => !c.EsOrigen && !Recientes.Contains(c))
        : _clientes!.Where(c => !c.EsOrigen && CultureInfo.CurrentCulture.CompareInfo.IndexOf(
            c.Nombre, _busqueda.Trim(), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);

    /// <summary>
    /// Vuelve a la misma ruta SIN query: los filtros con Ids del Tenant anterior
    /// (contrato del selector, § 4.2.4 e invariante I14) no sobreviven al cambio.
    /// El endpoint usa LocalRedirect, así que un valor que no sea una ruta local
    /// se rechaza allí. Se calcula al pintar y no al iniciar: el selector vive en
    /// el layout y sobrevive a las navegaciones del circuito.
    /// <para>
    /// Única excepción: en una ficha 360 por Id (<c>/trabajadores/{id}</c>, <c>/centros/{id}</c>,
    /// <c>/empresas/{id}</c>, <c>/clientes/{id}</c>) se añade la pista <c>?tenant={Tenant que se deja}</c>
    /// (contrato del selector, § 4.5, I15). La ficha pertenece al Tenant que se abandona; sin la pista el
    /// usuario aterriza en «No pudimos cargar…» sin salida. La pista es una coordenada de contexto, no
    /// autoridad: <c>EnlaceProfundoOtraEmpresa</c> solo ofrece «Abrir en…» si ese Tenant está en el
    /// conjunto autorizado del usuario y el cambio sigue siendo un POST revalidado por el servidor.
    /// No lleva Ids de filtros, así que no contradice I14.
    /// </para>
    /// </summary>
    private string RutaDeRetorno
    {
        get
        {
            var ruta = new Uri(NavigationManager.Uri).AbsolutePath;
            return _activo is not null && EsFichaPorId(ruta)
                ? $"{ruta}?tenant={_activo.TenantId}"
                : ruta;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex FichaPorId = new(
        "^/(trabajadores|centros|empresas|clientes)/[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}/?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool EsFichaPorId(string ruta) => FichaPorId.IsMatch(ruta);

    /// <summary>
    /// «Recientes»: los últimos Tenants abiertos con éxito, recortados SIEMPRE al conjunto autorizado que
    /// acaba de leer el componente (la cookie es una preferencia, no una autoridad). Fuera: el de origen
    /// (tiene su fila fija), el activo y, mientras se busca, todos (la búsqueda ve la cartera entera).
    /// </summary>
    private IReadOnlyList<ClienteAutorizadoDto> Recientes => _clientes is null || !string.IsNullOrWhiteSpace(_busqueda)
        ? []
        : _recientesIds
            .Select(id => _clientes.FirstOrDefault(c => c.TenantId == id))
            .OfType<ClienteAutorizadoDto>()
            .Where(c => !c.EsOrigen && c.TenantId != _activo?.TenantId)
            .Take(RecientesSelectorTenant.Maximo)
            .ToList();

    private int? Pendientes(ClienteAutorizadoDto cliente) =>
        !cliente.EsOrigen && _pendientes is not null && _pendientes.TryGetValue(cliente.TenantId, out var n) && n > 0 ? n : null;

    /// <summary>URL versionada del logo del Tenant (§ 4.1.5), o <c>null</c> para pintar las iniciales.</summary>
    private static string? UrlLogo(ClienteAutorizadoDto cliente) =>
        CaeManager.Web.Components.DesignSystem.AvatarTenant.UrlDeLogo(cliente.TenantId, cliente.LogoVersion);

    private string Empresas(int n) => n == 1
        ? Textos["SelectorTenantEmpresasUna"].Value
        : Textos["SelectorTenantEmpresasVarias", n].Value;

    private void Alternar()
    {
        _abierto = !_abierto;
        _busqueda = string.Empty;
        _enfocarBuscador = _abierto && _totalCartera >= UmbralBusqueda;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Tras el primer pintado y fuera del camino de render: el selector ya está en pantalla y un
        // contador lento o fallido no lo toca.
        if (!_pendientesIniciados && _activo is not null && _usuarioId is not null && _clientes is not null
            && ClientesAutorizados.SelectorVisible(_clientes, _activo))
        {
            _pendientesIniciados = true;
            _ = CargarPendientesAsync(_usuarioId.Value, _activo.TenantId, _clientes);
        }

        if (!_enfocarBuscador) return;

        _enfocarBuscador = false;
        try
        {
            await _buscador.FocusAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSException or JSDisconnectedException or TaskCanceledException)
        {
            // Sin buscador en el DOM o circuito cerrado: el foco es una comodidad.
        }
    }

    private void Cerrar()
    {
        _abierto = false;
        _busqueda = string.Empty;
    }

    private void AlPulsarTecla(KeyboardEventArgs e)
    {
        if (_abierto && e.Key == "Escape")
            Cerrar();
    }

    protected override void OnInitialized() => NavigationManager.LocationChanged += AlNavegar;

    private void AlNavegar(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e) =>
        _ = InvokeAsync(() =>
        {
            _abierto = false;
            StateHasChanged();
        });

    public void Dispose()
    {
        _desechado = true;
        NavigationManager.LocationChanged -= AlNavegar;
    }

    /// <summary>
    /// Pide los pendientes por empresa gestionada. El cálculo es el de Mi trabajo
    /// (<see cref="ObtenerMiTrabajoAgregadoQuery"/>, con su autorización y su RLS por Tenant dentro de la
    /// Application) detrás de una caché de vida corta; este componente no consulta ningún Tenant. Cualquier
    /// fallo o espera agotada deja el selector sin contador.
    /// </summary>
    private async Task CargarPendientesAsync(Guid usuarioId, Guid tenantActivoId, IReadOnlyList<ClienteAutorizadoDto> autorizados)
    {
        try
        {
            using var tope = new CancellationTokenSource(EsperaMaximaPendientes);
            var pendientes = await ContadorPendientes.ObtenerAsync(
                usuarioId, tenantActivoId, autorizados,
                ct => Mediator.Send(new ObtenerMiTrabajoAgregadoQuery(), ct), tope.Token);
            if (pendientes is null || _desechado) return;

            await InvokeAsync(() =>
            {
                _pendientes = pendientes;
                StateHasChanged();
            });
        }
        catch (Exception ex)
        {
            // Un contador es una comodidad: ni siquiera un circuito caído merece más que una traza.
            Logger.LogWarning(ex, "SelectorClienteActivo descartó el contador de pendientes: {TipoExcepcion}", ex.GetType().Name);
        }
    }

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _clientes = await Mediator.Send(new ObtenerClientesAutorizadosQuery());
        }
        catch (Exception ex) when (ExcepcionDeCircuitoDesconectado.Es(ex))
        {
            // El circuito de Blazor puede desconectarse (y con él el
            // IServiceProvider del scope, del que el pipeline de MediatR
            // resuelve sus propios behaviors, y el CaeManagerDbContext
            // scoped que hay debajo) mientras este despacho sigue en
            // vuelo — reproducido en producción (2026-08-17, mismo evento
            // que MainLayout/SelectorTema) y ampliado en
            // ExcepcionDeCircuitoDesconectado (REC-166: la misma carrera
            // también puede llegar como una NpgsqlException cruda de
            // desincronización de protocolo). Sin lista de clientes no hay
            // nada más que calcular aquí, y no hay nadie al otro lado
            // esperando el resultado de todos modos — pero sí queda
            // constancia de que ocurrió.
            Logger.LogWarning(ex, "SelectorClienteActivo descartó una excepción de desconexión de circuito: {TipoExcepcion} — {Mensaje}",
                ex.GetType().Name, ex.Message);
            return;
        }

        _activo = ClientesAutorizados.Activo(_clientes, ClienteActivoSeleccionado.TenantIdSeleccionado);
        _origen = _clientes.FirstOrDefault(c => c.EsOrigen);
        _totalCartera = ClientesAutorizados.TotalCartera(_clientes);

        _token = AntiforgeryStateProvider.GetAntiforgeryToken();

        try
        {
            _usuarioId = await CurrentUserService.ObtenerUsuarioActualIdAsync();
            if (_usuarioId is { } usuario)
                _recientesIds = LectorRecientes.Leer(usuario);
        }
        catch (Exception ex) when (ExcepcionDeCircuitoDesconectado.Es(ex))
        {
            // Sin usuario no hay recientes ni contador; el selector sigue funcionando.
        }
    }
}
