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
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;
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
    /// decisión 7 ter): por debajo, el buscador es ruido. El marco de Design del
    /// 20-10 (marco-talveg.js) lo pone con más de 10 Empresas gestionadas, es decir, desde 11.
    /// </summary>
    public const int UmbralBusqueda = 11;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IClienteActivoSeleccionado ClienteActivoSeleccionado { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private AntiforgeryStateProvider AntiforgeryStateProvider { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosComunes> Textos { get; set; } = default!;
    [Inject] private ILogger<ExcepcionDeCircuitoDesconectado> Logger { get; set; } = default!;
    [Inject] private ICurrentUserService CurrentUserService { get; set; } = default!;
    [Inject] private ContextoMarcoDelAmbito ContextoMarco { get; set; } = default!;
    [Inject] private IContadorPendientesSelectorTenant ContadorPendientes { get; set; } = default!;
    [Inject] private ILectorRecientesSelectorTenant LectorRecientes { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;
    [Inject] private CaeManager.Web.Components.DesignSystem.ToastService Toasts { get; set; } = default!;

    [Inject] private IServiceProvider ProveedorServicios { get; set; } = default!;

    private IReadOnlyList<ClienteAutorizadoDto>? _clientes;

    /// <summary>
    /// Lo que se pinta: una entrada por Asignación de Operación para quien llega por ella, sea cual sea
    /// su perfil (D-4 y O-2 (b), con <c>Servicio</c>), y sin papel el origen y la vía heredada. <see cref="_clientes"/> sigue decidiendo la visibilidad y los pendientes.
    /// </summary>
    private IReadOnlyList<ClienteAutorizadoDto> _entradas = [];

    /// <summary>La entrada activa (Tenant y papel) que enseña el disparador.</summary>
    private ClienteAutorizadoDto? _activo;

    /// <summary>El activo de la lista por Tenant, el de las reglas de visibilidad de siempre.</summary>
    private ClienteAutorizadoDto? _activoBase;

    private IReadOnlyDictionary<Guid, CaeManager.Domain.Operaciones.ServicioCae> _papelesRecordados =
        new Dictionary<Guid, CaeManager.Domain.Operaciones.ServicioCae>();
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
    private bool _puedeSolicitar;

    // Fijadas (Design, 2026-10-17). Viven en el localStorage del navegador (LIMITACIÓN CONOCIDA: por
    // usuario y por navegador, no viajan con la cuenta), y el módulo JS las lee y las guarda. Son una
    // preferencia: aquí se recortan siempre al conjunto que el servidor acaba de autorizar.
    private readonly List<Guid> _fijadas = [];

    // Lo guardado que hoy no está en la cartera autorizada (acceso caducado, otra cartera del mismo navegador):
    // no se pinta ni cuenta, pero tampoco se borra al guardar, por si la cartera vuelve a incluirlo.
    private readonly List<string> _guardadasAjenas = [];
    private IJSObjectReference? _moduloFijadas;
    private IJSObjectReference? _atajosFijadas;
    private bool _fijadasIniciadas;

    /// <summary>Cuántas de las fijadas llevan atajo Alt+cifra (Alt+1…Alt+5).</summary>
    public const int MaximoAtajos = 5;

    /// <summary>Solo se puede fijar con usuario conocido (la clave de almacenamiento) y con el módulo cargado.</summary>
    private bool PuedeFijar => _usuarioId is not null && _moduloFijadas is not null;

    /// <summary>Clave del almacenamiento local: una por usuario, para que dos cuentas del mismo navegador no se pisen.</summary>
    private string? ClaveFijadas => _usuarioId?.ToString("N");

    /// <summary>
    /// Ids de lo que se puede fijar y por tanto lleva atajo (la cartera, sin la organización propia), para el
    /// JS de Alt+1…5: lo que el servidor ya autorizó, nunca lo que diga el almacenamiento.
    /// </summary>
    private string IdsFijables => _clientes is null
        ? string.Empty
        : string.Join(",", _entradas.Where(c => !c.EsOrigen).Select(c => c.TenantId).Distinct());

    /// <summary>
    /// Decisión 1 sobre la lista por Tenant y, además (D-4), siempre que haya una entrada Inbound: el selector es el único
    /// sitio donde se elige ese papel.
    /// </summary>
    [MemberNotNullWhen(true, nameof(_activo), nameof(_activoBase))]
    private bool Visible => _activo is not null && _activoBase is not null
        && (ClientesAutorizados.SelectorVisible(_clientes!, _activoBase)
            || _entradas.Any(e => e.Servicio == CaeManager.Domain.Operaciones.ServicioCae.Inbound));

    /// <summary>
    /// Activa solo la entrada del Tenant y el papel en curso: una sola por listbox. Sin papel (origen o vía heredada, que no
    /// llegan por una Asignación de Operación) se compara null con null, así que la entrada sin papel del Tenant sigue activa;
    /// con un papel resuelto, la entrada sin papel del mismo Tenant no lo está.
    /// </summary>
    private bool EsActiva(ClienteAutorizadoDto entrada) =>
        entrada.TenantId == _activo!.TenantId && entrada.Servicio == _activo.Servicio;

    private string TextoPapel(CaeManager.Domain.Operaciones.ServicioCae papel) =>
        Textos[papel == CaeManager.Domain.Operaciones.ServicioCae.Inbound ? "SelectorTenantPapelInbound" : "SelectorTenantPapelOutbound"].Value;

    /// <summary>
    /// La entrada de un Tenant en Fijadas y Recientes (que van por Tenant): la del último papel elegido para él si sigue
    /// entre las autorizadas (D-4) y, si no, la primera. El papel recordado es una preferencia: se recorta a lo autorizado.
    /// </summary>
    private ClienteAutorizadoDto? EntradaPreferida(Guid tenantId)
    {
        var delTenant = _entradas.Where(e => !e.EsOrigen && e.TenantId == tenantId).ToList();
        return (_papelesRecordados.TryGetValue(tenantId, out var recordado)
                   ? delTenant.FirstOrDefault(e => e.Servicio == recordado)
                   : null)
               ?? delTenant.FirstOrDefault();
    }

    /// <summary>
    /// Las fijadas tal como se pintan: en el orden en que se fijaron, solo las que siguen en la cartera
    /// autorizada, y ninguna mientras se busca (la búsqueda ve la cartera entera, plana).
    /// </summary>
    private IReadOnlyList<ClienteAutorizadoDto> Fijadas => string.IsNullOrWhiteSpace(_busqueda) ? FijadasAutorizadas : [];

    /// <summary>
    /// Las fijadas en su orden, recortadas a la cartera autorizada (sin la organización propia), con independencia de
    /// la búsqueda. Es lo que lee Alt+1…5 desde <c>data-fijadas</c>: el mismo orden que ve la persona en pantalla.
    /// </summary>
    private IReadOnlyList<ClienteAutorizadoDto> FijadasAutorizadas => _clientes is null
        ? []
        : _fijadas
            .Select(EntradaPreferida)
            .OfType<ClienteAutorizadoDto>()
            .ToList();

    private string IdsFijadas => string.Join(",", FijadasAutorizadas.Select(c => c.TenantId));

    /// <summary>
    /// «tenantId:servicio» de las fijadas con papel (D-4): el que preselecciona <see cref="EntradaPreferida"/>, para que
    /// Alt+1…5 envíe el mismo papel que la fila de la fijada. Ya recortado a lo autorizado; el POST lo revalida.
    /// </summary>
    private string ServiciosFijadas => string.Join(",", FijadasAutorizadas
        .Where(c => c.Servicio is not null)
        .Select(c => $"{c.TenantId}:{c.Servicio}"));

    /// <summary>Hay una sección encima de la lista principal: entonces esta va sin rótulo visible.</summary>
    private bool HayGruposArriba => Fijadas.Count > 0 || Recientes.Count > 0;

    private bool Buscando => !string.IsNullOrWhiteSpace(_busqueda);

    /// <summary>Línea bajo el buscador: cuántas Empresas gestionadas coinciden, o la ayuda de teclado si no hay texto.</summary>
    private string AyudaDelBuscador => Buscando
        ? Textos["SelectorTenantConteo", Filtrados.Count(), _totalCartera].Value
        : Textos["SelectorTenantAyudaBuscar"].Value;

    private bool EsFijada(ClienteAutorizadoDto cliente) => _fijadas.Contains(cliente.TenantId);

    /// <summary>La entrada ya se pinta en Fijadas o Recientes (la otra entrada del mismo Tenant sigue en la lista).</summary>
    private bool YaArriba(ClienteAutorizadoDto entrada) => Fijadas.Contains(entrada) || Recientes.Contains(entrada);

    /// <summary>Tope de espera del contador: pasado, el selector sigue sin contador (nunca bloquea).</summary>
    public static readonly TimeSpan EsperaMaximaPendientes = TimeSpan.FromSeconds(10);

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
        ? _entradas.Where(c => !c.EsOrigen && !YaArriba(c))
            .OrderBy(c => c.Nombre, StringComparer.CurrentCulture)
        : _entradas.Where(c => !c.EsOrigen && CultureInfo.CurrentCulture.CompareInfo.IndexOf(
                c.Nombre, _busqueda.Trim(), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)
            .OrderBy(c => c.Nombre, StringComparer.CurrentCulture);

    /// <summary>
    /// Vuelve a la misma ruta SIN query: los filtros con Ids del Tenant anterior
    /// (contrato del selector, § 4.2.4 e invariante I14) no sobreviven al cambio.
    /// El endpoint usa LocalRedirect, así que un valor que no sea una ruta local
    /// se rechaza allí. Se calcula al pintar y no al iniciar: el selector vive en
    /// el layout y sobrevive a las navegaciones del circuito.
    /// <para>
    /// Desde una ficha por Id (<c>/trabajadores/{id}</c>, <c>/proyectos/{id}</c>…) se vuelve a la lista
    /// de ese árbol (<see cref="RutaTrasCambioDeTenant"/>, contrato del selector § 4.4): la ficha
    /// pertenece al Tenant que se deja y en el nuevo solo pintaría el estado vacío de «sin acceso».
    /// </para>
    /// </summary>
    private string RutaDeRetorno => RutaTrasCambioDeTenant.Calcular(new Uri(NavigationManager.Uri).AbsolutePath);

    /// <summary>
    /// «Recientes»: los últimos Tenants abiertos con éxito, recortados SIEMPRE al conjunto autorizado que
    /// acaba de leer el componente (la cookie es una preferencia, no una autoridad). Fuera: el de origen
    /// (tiene su fila fija), el activo y, mientras se busca, todos (la búsqueda ve la cartera entera).
    /// </summary>
    private IReadOnlyList<ClienteAutorizadoDto> Recientes => _clientes is null || !string.IsNullOrWhiteSpace(_busqueda)
        ? []
        : _recientesIds
            .Select(EntradaPreferida)
            .OfType<ClienteAutorizadoDto>()
            .Where(c => c.TenantId != _activo?.TenantId && !EsFijada(c))
            .Take(RecientesSelectorTenant.Maximo)
            .ToList();

    private int? Pendientes(ClienteAutorizadoDto cliente) =>
        // Los pendientes son los de Mi trabajo, que es de Outbound: una entrada Inbound no los lleva.
        !cliente.EsOrigen && cliente.Servicio != CaeManager.Domain.Operaciones.ServicioCae.Inbound && _pendientes is not null && _pendientes.TryGetValue(cliente.TenantId, out var n) && n > 0 ? n : null;

    /// <summary>URL versionada del logo del Tenant (§ 4.1.5), o <c>null</c> para pintar las iniciales.</summary>
    private static string? UrlLogo(ClienteAutorizadoDto cliente) =>
        CaeManager.Web.Components.DesignSystem.AvatarTenant.UrlDeLogo(cliente.TenantId, cliente.LogoVersion);

    private void Alternar()
    {
        _abierto = !_abierto;
        _busqueda = string.Empty;
        _enfocarBuscador = _abierto && _totalCartera >= UmbralBusqueda;

        // Los pendientes se piden al abrir la lista por primera vez, no al cargar la página: el circuito
        // tiene una sola puerta de acceso a datos y el cálculo (el de Mi trabajo, Tenant a Tenant) la
        // ocupa entera; pedirlo con cada carga competiría con las consultas de la propia página. Abrir es
        // un gesto del usuario, y cambiar de empresa es un POST del navegador que no pasa por el circuito.
        if (_abierto && !_pendientesIniciados && _activo is not null && _usuarioId is not null && _clientes is not null)
        {
            _pendientesIniciados = true;
            _ = CargarPendientesAsync(_usuarioId.Value, _activo.TenantId, _clientes);
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await IniciarFijadasAsync();

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

    /// <summary>
    /// Carga las fijadas del navegador y registra Alt+1…5, una sola vez y solo cuando el selector se pinta
    /// (con una sola empresa no hay selector ni atajos). Todo es comodidad: si el interop falla, el selector
    /// sigue funcionando sin estrellas.
    /// </summary>
    private async Task IniciarFijadasAsync()
    {
        if (_fijadasIniciadas || _desechado || _activo is null || _clientes is null || ClaveFijadas is not { } clave
            || !ClientesAutorizados.SelectorVisible(_clientes, _activo))
            return;

        _fijadasIniciadas = true;
        try
        {
            var modulo = await JsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/selector-tenant-fijadas.js");
            var guardadas = await modulo.InvokeAsync<string[]>("leerFijadas", clave);
            var atajos = await modulo.InvokeAsync<IJSObjectReference>("registrarAtajos");

            // El componente pudo desecharse mientras esperaba el interop (cajón móvil que monta y desmonta el
            // selector): lo recién creado no tiene dueño y el listener de document quedaría sin liberar.
            if (_desechado)
            {
                if (atajos is not null)
                {
                    await atajos.InvokeVoidAsync("dispose");
                    await atajos.DisposeAsync();
                }

                await modulo.DisposeAsync();
                return;
            }

            _moduloFijadas = modulo;
            _atajosFijadas = atajos;

            // «Trabajando en …» (marco de Design): cambiar de empresa es un POST con recarga completa, así que el
            // aviso lo da la página nueva al ver que la empresa activa ya no es la de la última carga de esta pestaña.
            await AvisarSiCambioDeEmpresaAsync(modulo, clave);

            var permitidas = _clientes.Where(c => !c.EsOrigen).Select(c => c.TenantId).ToHashSet();
            foreach (var texto in guardadas ?? []) // null si el interop no responde
            {
                if (Guid.TryParse(texto, out var id) && permitidas.Contains(id))
                {
                    if (!_fijadas.Contains(id)) _fijadas.Add(id);
                }
                else if (!string.IsNullOrWhiteSpace(texto) && !_guardadasAjenas.Contains(texto))
                {
                    _guardadasAjenas.Add(texto);
                }
            }

            StateHasChanged();
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSException or JSDisconnectedException or TaskCanceledException)
        {
            // Sin módulo o con el circuito cerrado: sin fijadas.
            _moduloFijadas = null;
        }
    }

    /// <summary>
    /// Aparte de la carga de fijadas: es un aviso cosmético y, si el navegador tuviera en caché una versión del módulo sin
    /// esta función, no debe llevarse por delante las estrellas ni los atajos.
    /// </summary>
    private async Task AvisarSiCambioDeEmpresaAsync(IJSObjectReference modulo, string clave)
    {
        try
        {
            if (_activo is not null && await modulo.InvokeAsync<bool>("cambioDeContexto", clave, _activo.TenantId.ToString()))
                Toasts.Mostrar(Textos["SelectorTenantTrabajandoEn", _activo.Nombre].Value);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSException or JSDisconnectedException or TaskCanceledException)
        {
            // Sin aviso: no es una función del selector, solo una cortesía.
        }
    }

    private async Task AlternarFijadaAsync(Guid tenantId)
    {
        if (!PuedeFijar || ClaveFijadas is not { } clave
            || _clientes?.FirstOrDefault(c => c.TenantId == tenantId) is not { EsOrigen: false })
            return;

        if (!_fijadas.Remove(tenantId))
            _fijadas.Add(tenantId);

        try
        {
            if (_moduloFijadas is not null)
                await _moduloFijadas.InvokeVoidAsync("guardarFijadas", clave, _fijadas.Select(id => id.ToString()).Concat(_guardadasAjenas).ToArray());
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSException or JSDisconnectedException or TaskCanceledException)
        {
            // La estrella vale hasta recargar: guardarla es una comodidad.
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

    // Con IAsyncDisposable, el renderizador llama solo a esta y no a Dispose(): se repite aquí la baja del evento.
    public async ValueTask DisposeAsync()
    {
        Dispose();
        try
        {
            if (_atajosFijadas is not null)
            {
                await _atajosFijadas.InvokeVoidAsync("dispose");
                await _atajosFijadas.DisposeAsync();
            }

            if (_moduloFijadas is not null)
                await _moduloFijadas.DisposeAsync();
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or InvalidOperationException or TaskCanceledException)
        {
            // El circuito puede cerrarse antes de este Dispose (mismo motivo que AtajosGlobales).
        }
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
        var tenantSeleccionado = ClienteActivoSeleccionado.TenantIdSeleccionado;
        try
        {
            // Del contexto del marco: un despacho por ámbito compartido con el menú y las migas, y en el circuito lo que
            // dejó el prerenderizado si es del mismo contexto exacto (ContextoMarcoDelAmbito). Solo presentación: el
            // POST vuelve a autorizar la selección y mantiene su antiforgery propio.
            var marco = await ContextoMarco.ObtenerAsync();
            _clientes = marco.ClientesAutorizados;
            _puedeSolicitar = marco.ParticipaEnIncorporacionCartera;
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

        _activoBase = ClientesAutorizados.Activo(_clientes, tenantSeleccionado);
        _origen = _clientes.FirstOrDefault(c => c.EsOrigen);
        _entradas = _clientes;

        _token = AntiforgeryStateProvider.GetAntiforgeryToken();

        PapelActivo papel = PapelActivo.SinPapel;
        try
        {
            _usuarioId = await CurrentUserService.ObtenerUsuarioActualIdAsync();
            if (_usuarioId is { } usuario)
                _recientesIds = LectorRecientes.Leer(usuario);

            // Selector de papel (D-4; O-2 (b), Chris 2026-11-17): lo tiene quien llega a un Tenant por una Asignación de
            // Operación, sea cual sea su perfil; no se decide aquí por el rol, sino en la consulta, con los mismos
            // predicados que el POST. Sin ninguna (el Administrador del Tenant propio, la vía heredada) la lista vuelve
            // sin papel y el selector queda como estaba.
            _entradas = ConPapeles(_clientes, await Mediator.Send(new ObtenerClientesAutorizadosQuery { PorPapel = true }));
            papel = await CurrentUserService.ObtenerPapelActivoAsync();
            if (_usuarioId is { } dueno && ProveedorServicios.GetService<ILectorPapelRecordadoSelectorTenant>() is { } lector)
                _papelesRecordados = lector.Leer(dueno);
        }
        catch (Exception ex) when (ExcepcionDeCircuitoDesconectado.Es(ex))
        {
            // Sin usuario no hay recientes ni contador; el selector sigue funcionando con la lista por Tenant.
        }

        _activo = EntradaActiva(tenantSeleccionado, papel) ?? _activoBase;
        _totalCartera = _entradas.Where(c => !c.EsOrigen).Select(c => c.TenantId).Distinct().Count();
    }

    /// <summary>
    /// La lista del marco (por Tenant, la que comparten menú y migas y la que viaja del prerenderizado al circuito) con
    /// el papel de cada Asignación de Operación: cada Tenant del marco se sustituye por sus entradas con papel, con el
    /// nombre y el logo del marco, y se añaden al final, en orden, los Tenants que solo alcanza la vía Inbound. Un Tenant
    /// del marco sin entradas con papel (vía heredada, o lista por papel vacía tras una desconexión) queda como estaba, y
    /// uno que llega por la vía heredada y además por Inbound conserva su entrada sin papel junto a la de Inbound: la vía
    /// heredada sigue siendo una opción autorizada y la única desde la que se vuelve a ella.
    /// </summary>
    internal static IReadOnlyList<ClienteAutorizadoDto> ConPapeles(
        IReadOnlyList<ClienteAutorizadoDto> marco, IReadOnlyList<ClienteAutorizadoDto> porPapel)
    {
        var resultado = new List<ClienteAutorizadoDto>();
        foreach (var cliente in marco)
        {
            var delTenant = cliente.EsOrigen
                ? []
                : porPapel.Where(p => !p.EsOrigen && p.TenantId == cliente.TenantId).ToList();
            if (!delTenant.Any(p => p.Servicio is not null)) resultado.Add(cliente);
            else resultado.AddRange(delTenant.Select(p => cliente with { Servicio = p.Servicio }));
        }

        var delMarco = marco.Select(c => c.TenantId).ToHashSet();
        resultado.AddRange(porPapel.Where(p => !p.EsOrigen && p.Servicio is not null && !delMarco.Contains(p.TenantId)));
        return resultado;
    }

    /// <summary>
    /// La entrada del Tenant seleccionado con el papel que resolvió el servidor para esta petición
    /// (<see cref="ICurrentUserService.ObtenerPapelActivoAsync"/>, nunca el cliente) o, si no hay entrada con papel para
    /// él, la entrada sin papel (vía heredada). Sin ninguna, el activo por Tenant de siempre.
    /// </summary>
    private ClienteAutorizadoDto? EntradaActiva(Guid? seleccionado, PapelActivo papel)
    {
        if (seleccionado is null) return null;
        CaeManager.Domain.Operaciones.ServicioCae? servicio = papel switch
        {
            PapelActivo.Outbound => CaeManager.Domain.Operaciones.ServicioCae.Outbound,
            PapelActivo.Inbound => CaeManager.Domain.Operaciones.ServicioCae.Inbound,
            _ => null,
        };
        var delTenant = _entradas.Where(e => !e.EsOrigen && e.TenantId == seleccionado).ToList();
        return delTenant.FirstOrDefault(e => servicio is not null && e.Servicio == servicio)
               ?? delTenant.FirstOrDefault(e => e.Servicio is null);
    }
}
