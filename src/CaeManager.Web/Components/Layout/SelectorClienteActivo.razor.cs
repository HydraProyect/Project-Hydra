using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Recursos;
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

    private IReadOnlyList<ClienteAutorizadoDto>? _clientes;
    private ClienteAutorizadoDto? _activo;
    private ClienteAutorizadoDto? _origen;
    private ElementReference _buscador;
    private bool _enfocarBuscador;
    private int _totalCartera;
    private AntiforgeryRequestToken? _token;
    private bool _abierto;
    private string _busqueda = string.Empty;

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
        ? _clientes!.Where(c => !c.EsOrigen)
        : _clientes!.Where(c => !c.EsOrigen && CultureInfo.CurrentCulture.CompareInfo.IndexOf(
            c.Nombre, _busqueda.Trim(), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);

    /// <summary>
    /// Vuelve a la misma ruta SIN query: los filtros con Ids del Tenant anterior
    /// (contrato del selector, § 4.2.4 e invariante I14) no sobreviven al cambio.
    /// El endpoint usa LocalRedirect, así que un valor que no sea una ruta local
    /// se rechaza allí. Se calcula al pintar y no al iniciar: el selector vive en
    /// el layout y sobrevive a las navegaciones del circuito.
    /// </summary>
    private string RutaDeRetorno => new Uri(NavigationManager.Uri).AbsolutePath;

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

    public void Dispose() => NavigationManager.LocationChanged -= AlNavegar;

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
    }
}
