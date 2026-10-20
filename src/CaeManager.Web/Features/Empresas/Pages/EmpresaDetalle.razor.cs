using CaeManager.Web.Features.Documentos.Components;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerSelloEmpresa;
using CaeManager.Application.Empresas.Commands.GuardarNotaInternaEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerCatalogoTrabajadoresEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerClientesDeEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerCredencialAccesoEmpresaSinContrasena;
using CaeManager.Application.Empresas.Queries.ObtenerCumplimientoEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Components;
using CaeManager.Web.Features.Centros.Components;
using CaeManager.Web.Services;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
namespace CaeManager.Web.Features.Empresas.Pages;

/// <summary>
/// Empresa 360 (decisión del propietario 2026-09-22: cada 360 tiene página
/// propia además del panel de 520 px). Compone, con las MISMAS consultas que
/// <c>EmpresaWorkspacePanel</c> —mismo alcance (<c>IAlcanceDatosService</c>) y
/// misma RLS—, la cabecera de cumplimiento, la lista de trabajadores de la
/// Empresa y, como pestañas, los mismos componentes que el panel
/// (Documentación, Agenda e Historial; Sello dentro de Ficha). La edición de la identidad y de
/// la credencial sigue viviendo en el panel: «Editar» lo abre.
///
/// <para>
/// <b>Recuentos por trabajador.</b> Cada trabajador cuenta una sola vez, en el
/// peor estado de sus documentos, que es el estado que
/// <see cref="ObtenerTrabajadoresQuery"/> ya deriva en SQL (peor vencimiento).
/// Ese handler no filtra por dos estados a la vez, pero ordenado por estado
/// (<c>OrdenarPor = EstadoDocumental</c>) deja cada grupo contiguo —Vencido,
/// Urgente, Próximo, Vigente, Sin caducidad—, así que cada chip de filtro es
/// un TRAMO de esa lista ordenada: con tres recuentos (Vencido, Urgente,
/// Próximo) y el total se sabe dónde empieza y acaba cada tramo, y cada página
/// se pide en páginas de 20 de la lista entera. Coste por carga: cuatro
/// consultas que devuelven 20 + 1 + 1 + 1 filas, sea cual sea el tamaño de la
/// plantilla; cada cambio de página o de chip, una o dos más.
/// </para>
///
/// <para>
/// Lo que estos recuentos NO ven, y la pantalla lo dice: el handler nunca
/// produce <see cref="EstadoDocumento.Faltante"/> (un documento obligatorio que
/// falta no tiene fecha que comparar), y los documentos de la propia Empresa
/// no son de ningún trabajador.
/// </para>
/// </summary>
public partial class EmpresaDetalle : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    internal const string PestanaTrabajadores = "trabajadores";
    internal const string PestanaClientes = "clientes";
    internal const string PestanaDocumentacion = "documentacion";
    internal const string PestanaAgenda = "agenda";
    internal const string PestanaFicha = "ficha";
    internal const string PestanaHistorial = "historial";

    internal const int TamanoPagina = 20;

    private static readonly string[] PestanasValidas =
        [PestanaTrabajadores, PestanaClientes, PestanaDocumentacion, PestanaAgenda, PestanaFicha, PestanaHistorial];

    [Parameter] public Guid EmpresaId { get; set; }

    /// <summary>Pestaña activa, en la URL (sin parámetro = Trabajadores), como los filtros de Centro 360.</summary>
    [Parameter, SupplyParameterFromQuery(Name = "pestana")] public string? Pestana { get; set; }

    /// <summary>Chip de estado de la pestaña Trabajadores, en la URL (sin parámetro = Todos).</summary>
    [Parameter, SupplyParameterFromQuery(Name = "estado")] public string? Estado { get; set; }

    /// <summary>Texto del buscador de la pestaña Trabajadores, en la URL (<c>?q=</c>), como en Centro 360.</summary>
    [Parameter, SupplyParameterFromQuery(Name = "q")] public string? Busqueda { get; set; }

    /// <summary>Delegación y Oficio de la pestaña Trabajadores (Ids del catálogo de esta Empresa) y su agrupación, en la URL.</summary>
    [Parameter, SupplyParameterFromQuery(Name = "delegacion")] public string? DelegacionEmpresa { get; set; }

    [Parameter, SupplyParameterFromQuery(Name = "oficio")] public string? Oficio { get; set; }

    [Parameter, SupplyParameterFromQuery(Name = "agrupar")] public string? Agrupar { get; set; }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ContextWorkspaceService WorkspaceService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private EmpresaDetalleDto? _detalle;
    private int? _cumplimiento;
    private bool _cargando = true;
    private bool _error;
    /// <summary>
    /// La consulta devolvió null: la ficha no está en el Tenant activo (o no existe; es el mismo estado, § 4.5).
    /// No es un fallo transitorio, así que no lleva «Reintentar»: lo pinta <c>FichaNoEncontrada</c>.
    /// </summary>
    private bool _noEncontrada;

    /// <summary>Todos los Trabajadores de la Empresa: lo que cuentan la cabecera y el contador de la pestaña.</summary>
    private RecuentoTrabajadores? _recuentos;

    /// <summary>
    /// Los que casan con el buscador: lo que cuentan los chips y de donde salen los tramos de la lista. Sin texto en
    /// el buscador es el mismo recuento que <see cref="_recuentos"/>.
    /// </summary>
    private RecuentoTrabajadores? _recuentosLista;
    private IReadOnlyList<TrabajadorListaDto> _filas = [];
    private bool _cargandoTrabajadores = true;
    private bool _errorTrabajadores;
    private int _pagina = 1;

    /// <summary>
    /// Páginas ya pedidas de la lista entera ordenada por estado, por número
    /// de página. Se vacía en cada recarga de los recuentos: una página
    /// guardada de una carga anterior podría estar desplazada respecto a los
    /// recuentos nuevos.
    /// </summary>
    private readonly Dictionary<int, IReadOnlyList<TrabajadorListaDto>> _paginasOrdenadas = [];

    private IReadOnlyList<ClienteDeEmpresaDto>? _clientes;
    private bool _cargandoClientes;
    private bool _errorClientes;

    [CascadingParameter] private Task<AuthenticationState>? EstadoAutenticacion { get; set; }
    // Con escritura, la caja «Sello» existe aunque no haya sello: ahí está la zona de subida del primero.
    private bool _puedeEscribir;
    private SelloEmpresaDto? _sello;
    private bool HayCajaAcceso => UrlPortalCae.Normalizar(_credencial?.UrlAcceso) is not null || _errorAcceso || _accesoRequiereDosFactores;
    private bool HayCajaSello => _sello is not null || _puedeEscribir;
    private bool _editorFichaVisible;
    private bool HayCajasDeFicha => HayCajaAcceso || HayCajaSello || _detalle?.FichaVisible == true;
    private CredencialAccesoEmpresaSinContrasenaDto? _credencial;
    private bool _cargandoAcceso = true;
    private bool _errorAcceso;
    // P1-I1: el rol lee credenciales, pero la cuenta no tiene 2FA (ver
    // SegundoFactorRequeridoParaCredencialesException). Se avisa en la tarjeta en
    // vez de redirigir: abrir la ficha no es pedir la credencial.
    private bool _accesoRequiereDosFactores;

    private string _pestana = PestanaTrabajadores;
    private FiltroTrabajadores _filtro = FiltroTrabajadores.Todos;
    private string? _pestanaDeLaUrl;
    private string? _estadoDeLaUrl;
    private string _busqueda = string.Empty;
    private string? _busquedaDeLaUrl;
    private bool _urlAdoptada;

    internal const string AgruparPorDelegacionEmpresa = "delegacion";
    internal const string AgruparPorOficio = "oficio";

    // Delegación y Oficio (catálogo de ESTA Empresa) y la agrupación de la lista: filtros y vista de la pestaña.
    private string _delegacionId = string.Empty;
    private string _oficioId = string.Empty;
    private string _agrupar = string.Empty;
    private string? _delegacionDeLaUrl;
    private string? _oficioDeLaUrl;
    private string? _agruparDeLaUrl;

    /// <summary>Catálogo de Delegación y Oficio de la Empresa, para las pastillas. <c>null</c> hasta cargarlos; si fallan, vacío.</summary>
    private CatalogoTrabajadoresEmpresaDto? _catalogo;

    private bool Agrupado => _agrupar.Length > 0;
    private bool HayFiltroDeCatalogo => _delegacionId.Length > 0 || _oficioId.Length > 0;
    private Guid? DelegacionEmpresaFiltro => Guid.TryParse(_delegacionId, out var id) ? id : null;
    private Guid? OficioFiltro => Guid.TryParse(_oficioId, out var id) ? id : null;

    private static string IdValido(string? valor) => Guid.TryParse(valor, out var id) ? id.ToString() : string.Empty;

    private static string AgrupacionValida(string? valor) =>
        string.Equals(valor, AgruparPorDelegacionEmpresa, StringComparison.OrdinalIgnoreCase) ? AgruparPorDelegacionEmpresa
        : string.Equals(valor, AgruparPorOficio, StringComparison.OrdinalIgnoreCase) ? AgruparPorOficio
        : string.Empty;

    /// <summary>
    /// Empresa que esta instancia tiene cargada: Blazor reutiliza la instancia
    /// al pasar de una Empresa a otra, y sin esto cada cambio de pestaña o de
    /// chip en la URL relanzaría la carga entera.
    /// </summary>
    private Guid? _empresaCargada;

    /// <summary>
    /// Generación de la Empresa que se enseña y un contador por carga (patrón
    /// de CentroDetalle): cada respuesta compara el suyo antes de escribir y,
    /// si ya no es el vigente, se descarta.
    /// </summary>
    private int _generacion;
    private int _cargaDetalle;
    private int _cargaTrabajadores;
    private int _cargaPagina;
    private int _cargaClientes;
    private int _cargaAcceso;

    private readonly CancellationTokenSource _ciclo = new();
    private CancellationToken _cancelacion;

    private IReadOnlyList<BreadcrumbElemento> Miguero =>
        [new BreadcrumbElemento(Textos["MigaEmpresas"]), new BreadcrumbElemento(_detalle?.RazonSocial ?? "…")];

    protected override void OnInitialized() => _cancelacion = _ciclo.Token;

    /// <summary>
    /// Solo recarga cuando cambia la Empresa. La pestaña y el chip también
    /// llegan por aquí (viajan en la URL): la pestaña Clientes se carga la
    /// primera vez que se abre, y un chip nuevo pide solo su página.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        var (filtroCambio, busquedaCambio, catalogoCambio, agruparCambio) = AdoptarUrl();

        if (_empresaCargada != EmpresaId)
        {
            _empresaCargada = EmpresaId;
            ReiniciarParaNuevaEmpresa();
            await CargarAsync();
            return;
        }

        // Delegación y Oficio cambian lo que cuentan los chips, igual que el buscador: recuentos y lista otra vez.
        if ((busquedaCambio || catalogoCambio) && _recuentos is not null)
        {
            _pagina = 1;
            await CargarTrabajadoresAsync(soloLaLista: true);
        }
        else if ((filtroCambio || agruparCambio) && _recuentos is not null)
        {
            _pagina = 1;
            await MostrarPaginaAsync();
        }

        if (_pestana == PestanaFicha && !_cargandoAcceso && !HayCajasDeFicha)
            _pestana = PestanaTrabajadores;

        await CargarClientesSiHaceFaltaAsync();
    }

    /// <summary>
    /// Adopta pestaña y chip cuando cambian en la URL AHÍ FUERA —un enlace, el
    /// botón «atrás»—; lo que cambia la propia página ya lo apuntó antes de
    /// navegar. Devuelve si el chip cambió y si cambió el texto del buscador.
    /// </summary>
    private (bool Filtro, bool Busqueda, bool Catalogo, bool Agrupar) AdoptarUrl()
    {
        var filtroCambio = false;
        var busquedaCambio = false;
        var catalogoCambio = false;
        var agruparCambio = false;
        if (!_urlAdoptada || !string.Equals(_pestanaDeLaUrl, Pestana, StringComparison.Ordinal))
        {
            _pestanaDeLaUrl = Pestana;
            _pestana = Pestana == "sello" ? PestanaFicha : PestanasValidas.Contains(Pestana) ? Pestana! : PestanaTrabajadores;
        }

        if (!_urlAdoptada || !string.Equals(_estadoDeLaUrl, Estado, StringComparison.Ordinal))
        {
            _estadoDeLaUrl = Estado;
            var filtro = FiltroDesdeUrl(Estado);
            filtroCambio = filtro != _filtro;
            _filtro = filtro;
        }

        if (!_urlAdoptada || !string.Equals(_busquedaDeLaUrl, Busqueda, StringComparison.Ordinal))
        {
            _busquedaDeLaUrl = Busqueda;
            var busqueda = Busqueda ?? string.Empty;
            busquedaCambio = !string.Equals(busqueda, _busqueda, StringComparison.Ordinal);
            _busqueda = busqueda;
        }

        if (!_urlAdoptada || !string.Equals(_delegacionDeLaUrl, DelegacionEmpresa, StringComparison.Ordinal)
            || !string.Equals(_oficioDeLaUrl, Oficio, StringComparison.Ordinal))
        {
            _delegacionDeLaUrl = DelegacionEmpresa;
            _oficioDeLaUrl = Oficio;
            var delegacion = IdValido(DelegacionEmpresa);
            var oficio = IdValido(Oficio);
            catalogoCambio = delegacion != _delegacionId || oficio != _oficioId;
            _delegacionId = delegacion;
            _oficioId = oficio;
        }

        if (!_urlAdoptada || !string.Equals(_agruparDeLaUrl, Agrupar, StringComparison.Ordinal))
        {
            _agruparDeLaUrl = Agrupar;
            var agrupar = AgrupacionValida(Agrupar);
            agruparCambio = agrupar != _agrupar;
            _agrupar = agrupar;
        }

        _urlAdoptada = true;
        return (filtroCambio, busquedaCambio, catalogoCambio, agruparCambio);
    }

    private void InvalidarCargas()
    {
        _generacion++;
        _cargaDetalle++;
        _cargaTrabajadores++;
        _cargaPagina++;
        _cargaClientes++;
        _cargaAcceso++;
        _cargaContacto++;
    }

    /// <summary>Nada de la Empresa anterior sobrevive al cambio.</summary>
    private void ReiniciarParaNuevaEmpresa()
    {
        InvalidarCargas();
        _editorFichaVisible = false;
        _detalle = null;
        _cumplimiento = null;
        _error = false;
        _recuentos = null;
        _recuentosLista = null;
        _catalogo = null;
        _filas = [];
        _paginasOrdenadas.Clear();
        _cargandoTrabajadores = true;
        _errorTrabajadores = false;
        _pagina = 1;
        _clientes = null;
        _contactoPrincipal = null;
        _cargandoClientes = false;
        _errorClientes = false;
        _credencial = null;
        _sello = null;
        _cargandoAcceso = true;
        _errorAcceso = false;
    }

    private DrawerGestionDocumento _drawerGestion = default!;

    private Task SubirDocumentoAsync() => _drawerGestion.AbrirCrearParaEmpresaAsync(EmpresaId);

    private async Task ManejarDocumentoGuardadoAsync()
    {
        // Recarga completa: la pestaña Documentación, la cifra por trabajador y los recuentos enseñan el documento nuevo.
        NavigationManager.Refresh(forceReload: true);
        await Task.CompletedTask;
    }

    private async Task CargarAsync()
    {
        var carga = ++_cargaDetalle;
        var empresaId = EmpresaId;
        _cargando = true;
        _error = false;
        _noEncontrada = false;

        try
        {
            // Las mismas dos consultas que la cabecera del panel. Fuera de
            // alcance (o inexistente) ObtenerEmpresaPorIdQuery devuelve null,
            // y la página no distingue un caso del otro, igual que el panel.
            var detalle = await Mediator.Send(new ObtenerEmpresaPorIdQuery(empresaId), _cancelacion);
            if (carga != _cargaDetalle) return;
            if (detalle is null)
            {
                _detalle = null;
                _noEncontrada = true;
                return;
            }

            var cumplimiento = await Mediator.Send(new ObtenerCumplimientoEmpresaQuery(empresaId), _cancelacion);
            if (carga != _cargaDetalle) return;
            _detalle = detalle;
            _cumplimiento = cumplimiento;
        }
        catch (Exception)
        {
            if (carga == _cargaDetalle)
                _error = true;
            return;
        }
        finally
        {
            if (carga == _cargaDetalle)
                _cargando = false;
        }

        // Mismo criterio que SoloConEscritura (la zona de subida del sello): con escritura
        // la caja «Sello» se monta también sin sello; sin escritura, solo con sello.
        _puedeEscribir = await SoloConEscritura.PuedeEscribirAsync(EstadoAutenticacion);

        // La consulta existente de Sello conserva su puerta de gestión. null no distingue
        // ausencia de sello de falta de permiso ni de error: en ese caso, sin escritura, no hay caja.
        try
        {
            var sello = await Mediator.Send(new ObtenerSelloEmpresaQuery(empresaId), _cancelacion);
            if (carga != _cargaDetalle) return;
            _sello = sello;
        }
        catch (Exception)
        {
            if (carga != _cargaDetalle) return;
            _sello = null;
        }

        await CargarTrabajadoresAsync();
        // Los Clientes empresariales se piden siempre, no solo al abrir la pestaña:
        // de esta lista acotada por alcance sale el recuento de la cabecera y del
        // contador (EmpresaDetalleDto.ClienteIds no está acotado).
        await CargarClientesAsync();
        await CargarContactoPrincipalAsync();

        // Mismo motivo que CentroDetalle: OnParametersSetAsync también corre
        // en el prerenderizado estático, y una tarea sin await ahí queda en
        // vuelo cuando ASP.NET Core ya liberó el scope de DI.
        if (RendererInfo.IsInteractive && carga == _cargaDetalle)
            _ = CargarAccesoAsync();
    }

    /// <summary>
    /// Recuentos por estado y la página visible. Un fallo aquí no tumba la
    /// página: la Empresa sí cargó, y la pestaña enseña su propio error.
    /// <para>
    /// Con texto en el buscador se cuentan dos cosas: los que casan (chips y tramos de la lista) y todos (cabecera y
    /// contador de la pestaña). <paramref name="soloLaLista"/> reutiliza el recuento de todos que ya se tiene: es lo
    /// que cambia al teclear. Con una lista ya pintada, esta se queda a la vista hasta que llega la nueva, para no
    /// desmontar el buscador mientras se escribe.
    /// </para>
    /// </summary>
    private Task CargarTrabajadoresAsync() => CargarTrabajadoresAsync(soloLaLista: false);

    private async Task CargarTrabajadoresAsync(bool soloLaLista)
    {
        var carga = ++_cargaTrabajadores;
        // Una página pedida con el texto anterior ya no vale: que no escriba en la caché que se vacía aquí.
        _cargaPagina++;
        var empresaId = EmpresaId;
        var busqueda = TextoBuscado;
        _cargandoTrabajadores = _recuentos is null;
        _errorTrabajadores = false;
        _paginasOrdenadas.Clear();

        try
        {
            // Las pastillas «Delegación» y «Oficio» ofrecen el catálogo de esta Empresa: se pide una vez por Empresa.
            if (_catalogo is null)
            {
                var catalogo = await CargarCatalogoAsync(empresaId);
                if (carga != _cargaTrabajadores) return;
                _catalogo = catalogo;
            }

            var deLaLista = await ContarTodosLosEstadosAsync(empresaId, busqueda, carga);
            if (deLaLista is null) return;
            var (recuentoDeLaLista, primera) = deLaLista.Value;

            // «Todos» (cabecera y contador de la pestaña) son los de la Empresa entera: sin buscador ni catálogo.
            var todos = recuentoDeLaLista;
            if (busqueda is not null || HayFiltroDeCatalogo)
            {
                if (soloLaLista && _recuentos is { } yaContados)
                {
                    todos = yaContados;
                }
                else
                {
                    var deTodos = await ContarTodosLosEstadosAsync(empresaId, null, carga, conCatalogo: false);
                    if (deTodos is null) return;
                    todos = deTodos.Value.Recuento;
                }
            }

            _paginasOrdenadas[1] = primera;
            _recuentos = todos;
            _recuentosLista = recuentoDeLaLista;
            await MostrarPaginaAsync();
        }
        catch (Exception)
        {
            if (carga == _cargaTrabajadores)
            {
                _recuentos = null;
                _recuentosLista = null;
                _errorTrabajadores = true;
            }
        }
        finally
        {
            if (carga == _cargaTrabajadores)
                _cargandoTrabajadores = false;
        }
    }

    /// <summary>Total de un solo estado: el handler cuenta en SQL y devuelve una fila, que se descarta.</summary>
    private async Task<int> ContarAsync(Guid empresaId, EstadoDocumento estado, string? busqueda, bool conCatalogo)
    {
        var resultado = await Mediator.Send(new ObtenerTrabajadoresQuery(
            busqueda, EmpresaId: empresaId, Pagina: 1, TamanoPagina: 1, EstadoDocumental: estado.ToString(),
            DelegacionEmpresaId: conCatalogo ? DelegacionEmpresaFiltro : null, OficioEmpresaId: conCatalogo ? OficioFiltro : null), _cancelacion);
        return resultado.TotalElementos;
    }

    /// <summary>Catálogo de Delegación y Oficio de la Empresa. Si fallan, vacío: la lista sigue y las pastillas no se ofrecen.</summary>
    private async Task<CatalogoTrabajadoresEmpresaDto> CargarCatalogoAsync(Guid empresaId)
    {
        try
        {
            return await Mediator.Send(new ObtenerCatalogoTrabajadoresEmpresaQuery(empresaId), _cancelacion);
        }
        catch (OperationCanceledException) when (_cancelacion.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return CatalogoTrabajadoresEmpresaDto.Vacio;
        }
    }

    /// <summary>
    /// Recuento por estado de los Trabajadores que casan con <paramref name="busqueda"/> (<c>null</c>, todos) y su
    /// primera página ordenada. <c>null</c> si mientras tanto empezó otra carga: quien llama no escribe nada.
    /// </summary>
    private async Task<(RecuentoTrabajadores Recuento, IReadOnlyList<TrabajadorListaDto> PrimeraPagina)?> ContarTodosLosEstadosAsync(
        Guid empresaId, string? busqueda, int carga, bool conCatalogo = true)
    {
        var primera = await Mediator.Send(ConsultaOrdenada(empresaId, 1, busqueda, conCatalogo ? DelegacionEmpresaFiltro : null, conCatalogo ? OficioFiltro : null), _cancelacion);
        if (carga != _cargaTrabajadores) return null;
        var vencidos = await ContarAsync(empresaId, EstadoDocumento.Vencido, busqueda, conCatalogo);
        if (carga != _cargaTrabajadores) return null;
        var urgentes = await ContarAsync(empresaId, EstadoDocumento.Urgente, busqueda, conCatalogo);
        if (carga != _cargaTrabajadores) return null;
        var proximos = await ContarAsync(empresaId, EstadoDocumento.Proximo, busqueda, conCatalogo);
        if (carga != _cargaTrabajadores) return null;

        return (new RecuentoTrabajadores(primera.TotalElementos, vencidos, urgentes, proximos), primera.Elementos);
    }

    private static ObtenerTrabajadoresQuery ConsultaOrdenada(Guid empresaId, int pagina, string? busqueda, Guid? delegacionId, Guid? oficioId) =>
        new(busqueda, EmpresaId: empresaId, Pagina: pagina, TamanoPagina: TamanoPagina,
            OrdenarPor: nameof(TrabajadorListaDto.EstadoDocumental), DelegacionEmpresaId: delegacionId, OficioEmpresaId: oficioId,
            ConDesgloseDocumental: true);

    /// <summary>
    /// Estados que deja pasar cada chip, para pedir a la consulta la página agrupada (el orden por Delegación u Oficio
    /// ya no deja cada chip como un tramo de la lista ordenada por estado). Los mismos tramos que
    /// <see cref="Tramo"/>: «Al día» es todo lo que no es Vencido, Urgente ni Próximo.
    /// </summary>
    private static string? EstadosDelChip(FiltroTrabajadores filtro) =>
        EstadosDeChip(filtro) is { } estados ? string.Join(',', estados.Select(e => e.ToString())) : null;

    /// <summary>
    /// Única fuente de los estados de cada chip: la usan la consulta agrupada (<see cref="EstadosDelChip"/>) y el filtro
    /// de filas de la lista sin agrupar (<see cref="Incluye"/>), para que la página coincida con el «Mostrando» del recuento
    /// (<c>AlDia</c> es todo lo que no es Vencido, Urgente ni Próximo, así que incluye «Sin confirmar»).
    /// </summary>
    private static EstadoDocumento[]? EstadosDeChip(FiltroTrabajadores filtro) => filtro switch
    {
        FiltroTrabajadores.SinDocumentacionValida => [EstadoDocumento.Vencido],
        FiltroTrabajadores.PorVencer => [EstadoDocumento.Urgente, EstadoDocumento.Proximo],
        FiltroTrabajadores.AlDia => [EstadoDocumento.SinConfirmar, EstadoDocumento.Vigente, EstadoDocumento.SinCaducidad],
        _ => null
    };

    /// <summary>
    /// Pinta la página <see cref="_pagina"/> del chip activo: su tramo de la
    /// lista ordenada, pedido en páginas de esa lista (las ya pedidas no se
    /// repiten). Las filas se filtran además por su estado: si los documentos
    /// cambian entre el recuento y la página, sobra alguna fila en vez de
    /// colarse una de otro estado.
    /// </summary>
    private async Task MostrarPaginaAsync()
    {
        if (_recuentosLista is not { } recuentos) return;

        var carga = ++_cargaPagina;
        var generacion = _generacion;
        var empresaId = EmpresaId;
        var busqueda = TextoBuscado;
        var filtro = _filtro;
        var (desde, cantidad) = Tramo(recuentos, filtro, _pagina, TamanoPagina);
        if (cantidad <= 0)
        {
            _filas = [];
            return;
        }

        // Agrupada, la lista se ordena por Delegación u Oficio y cada chip filtra por sus estados en la propia consulta.
        if (Agrupado)
        {
            try
            {
                var agrupada = await Mediator.Send(new ObtenerTrabajadoresQuery(
                    busqueda, EmpresaId: empresaId, Pagina: _pagina, TamanoPagina: TamanoPagina,
                    OrdenarPor: _agrupar == AgruparPorOficio ? nameof(TrabajadorListaDto.OficioNombre) : nameof(TrabajadorListaDto.DelegacionEmpresaNombre),
                    EstadoDocumental: EstadosDelChip(filtro), DelegacionEmpresaId: DelegacionEmpresaFiltro, OficioEmpresaId: OficioFiltro,
                    ConDesgloseDocumental: true), _cancelacion);
                if (carga != _cargaPagina || generacion != _generacion) return;
                _filas = agrupada.Elementos;
            }
            catch (Exception)
            {
                if (carga == _cargaPagina && generacion == _generacion)
                {
                    _filas = [];
                    _errorTrabajadores = true;
                }
            }

            return;
        }

        try
        {
            var primeraPagina = desde / TamanoPagina + 1;
            var ultimaPagina = (desde + cantidad - 1) / TamanoPagina + 1;
            var filas = new List<TrabajadorListaDto>();
            for (var pagina = primeraPagina; pagina <= ultimaPagina; pagina++)
            {
                if (!_paginasOrdenadas.TryGetValue(pagina, out var elementos))
                {
                    var resultado = await Mediator.Send(ConsultaOrdenada(empresaId, pagina, busqueda, DelegacionEmpresaFiltro, OficioFiltro), _cancelacion);
                    if (carga != _cargaPagina || generacion != _generacion) return;
                    elementos = resultado.Elementos;
                    _paginasOrdenadas[pagina] = elementos;
                }

                filas.AddRange(elementos);
            }

            _filas = filas
                .Skip(desde - (primeraPagina - 1) * TamanoPagina)
                .Take(cantidad)
                .Where(t => Incluye(filtro, t.EstadoDocumental))
                .ToList();
        }
        catch (Exception)
        {
            if (carga == _cargaPagina && generacion == _generacion)
            {
                _filas = [];
                _errorTrabajadores = true;
            }
        }
    }

    /// <summary>
    /// Posición del chip en la lista ordenada por estado (Vencido, Urgente,
    /// Próximo, Vigente, Sin caducidad) y la porción de él que cae en
    /// <paramref name="pagina"/>.
    /// </summary>
    internal static (int Desde, int Cantidad) Tramo(RecuentoTrabajadores recuentos, FiltroTrabajadores filtro, int pagina, int tamano)
    {
        var finPorVencer = recuentos.Vencidos + recuentos.Urgentes + recuentos.Proximos;
        var (inicio, fin) = filtro switch
        {
            FiltroTrabajadores.SinDocumentacionValida => (0, recuentos.Vencidos),
            FiltroTrabajadores.PorVencer => (recuentos.Vencidos, finPorVencer),
            FiltroTrabajadores.AlDia => (finPorVencer, recuentos.Total),
            _ => (0, recuentos.Total)
        };

        var desde = inicio + (pagina - 1) * tamano;
        return (desde, Math.Max(0, Math.Min(tamano, fin - desde)));
    }

    internal static bool Incluye(FiltroTrabajadores filtro, EstadoDocumento? estado) =>
        EstadosDeChip(filtro) is not { } estados
        || (estado is null ? filtro == FiltroTrabajadores.AlDia : estados.Contains(estado.Value));

    private int TotalDelFiltro => _recuentosLista is not { } r ? 0 : _filtro switch
    {
        FiltroTrabajadores.SinDocumentacionValida => r.Vencidos,
        FiltroTrabajadores.PorVencer => r.PorVencer,
        FiltroTrabajadores.AlDia => r.AlDia,
        _ => r.Total
    };

    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(TotalDelFiltro / (double)TamanoPagina));

    private async Task CambiarPaginaAsync(int pagina)
    {
        _pagina = Math.Clamp(pagina, 1, TotalPaginas);
        await MostrarPaginaAsync();
    }

    private bool HayBusqueda => !string.IsNullOrWhiteSpace(_busqueda);

    /// <summary>Lo que se pide a la consulta: el texto del buscador sin espacios alrededor, o <c>null</c> si no hay.</summary>
    private string? TextoBuscado => HayBusqueda ? _busqueda.Trim() : null;

    private async Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        _pagina = 1;
        _busquedaDeLaUrl = string.IsNullOrWhiteSpace(valor) ? null : valor;
        NavigationManager.ActualizarFiltroEnUrl("q", valor);
        await CargarTrabajadoresAsync(soloLaLista: true);
    }

    /// <summary>
    /// Quita el texto y el chip, también de la URL y en una sola navegación: una lista sin filtro cuya URL siga
    /// llevándolos los repone en cuanto alguien recarga o comparte el enlace.
    /// </summary>
    private async Task QuitarFiltrosAsync()
    {
        _busqueda = string.Empty;
        _filtro = FiltroTrabajadores.Todos;
        _delegacionId = string.Empty;
        _oficioId = string.Empty;
        _pagina = 1;
        _busquedaDeLaUrl = null;
        _estadoDeLaUrl = null;
        _delegacionDeLaUrl = null;
        _oficioDeLaUrl = null;
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["q"] = null,
            ["estado"] = null,
            ["delegacion"] = null,
            ["oficio"] = null
        });
        await CargarTrabajadoresAsync(soloLaLista: true);
    }

    private async Task CambiarDelegacionEmpresaAsync(string valor)
    {
        _delegacionId = IdValido(valor);
        _pagina = 1;
        _delegacionDeLaUrl = _delegacionId.Length == 0 ? null : _delegacionId;
        NavigationManager.ActualizarFiltroEnUrl("delegacion", _delegacionId);
        await CargarTrabajadoresAsync(soloLaLista: true);
    }

    private async Task CambiarOficioAsync(string valor)
    {
        _oficioId = IdValido(valor);
        _pagina = 1;
        _oficioDeLaUrl = _oficioId.Length == 0 ? null : _oficioId;
        NavigationManager.ActualizarFiltroEnUrl("oficio", _oficioId);
        await CargarTrabajadoresAsync(soloLaLista: true);
    }

    private async Task CambiarAgrupacionAsync(string valor)
    {
        _agrupar = AgrupacionValida(valor);
        _pagina = 1;
        _agruparDeLaUrl = Agrupado ? _agrupar : null;
        NavigationManager.ActualizarFiltroEnUrl("agrupar", Agrupado ? _agrupar : null);
        await MostrarPaginaAsync();
    }

    private IReadOnlyList<OpcionEstado> OpcionesDelegacionEmpresa =>
        (_catalogo?.DelegacionesEmpresa ?? []).Select(d => new OpcionEstado(d.Id.ToString(), d.Nombre)).ToList();

    private IReadOnlyList<OpcionEstado> OpcionesOficio =>
        (_catalogo?.Oficios ?? []).Select(o => new OpcionEstado(o.Id.ToString(), o.Nombre)).ToList();

    private bool TieneDelegacionesEmpresa => _catalogo is { DelegacionesEmpresa.Count: > 0 };

    private bool TieneOficios => _catalogo is { Oficios.Count: > 0 };

    /// <summary>
    /// El bloque de pastillas se ofrece con catálogo, y también sin él mientras haya una agrupación activa (por
    /// <c>?agrupar=</c>): sin la pastilla «Agrupar» no habría forma de quitarla.
    /// </summary>
    private bool OfrecerPastillasDeCatalogo => TieneDelegacionesEmpresa || TieneOficios || Agrupado;

    /// <summary>«Por Delegación» y «Por Oficio» solo se ofrecen si ESE catálogo tiene opciones.</summary>
    private IReadOnlyList<OpcionEstado> OpcionesAgrupacion
    {
        get
        {
            var opciones = new List<OpcionEstado>();
            if (TieneDelegacionesEmpresa)
                opciones.Add(new OpcionEstado(AgruparPorDelegacionEmpresa, Textos["AgruparPorDelegacionEmpresa"].Value));
            if (TieneOficios)
                opciones.Add(new OpcionEstado(AgruparPorOficio, Textos["AgruparPorOficio"].Value));
            return opciones;
        }
    }

    /// <summary>
    /// Las filas de la página en grupos contiguos (el orden ya viene de la consulta). Sin agrupar, un solo grupo sin
    /// título. Quien no tiene Delegación u Oficio va a «Sin delegación» / «Sin oficio», al final.
    /// </summary>
    private IReadOnlyList<(string? Titulo, IReadOnlyList<TrabajadorListaDto> Filas)> GruposDeFilas()
    {
        if (!Agrupado)
            return [(null, _filas)];

        var grupos = new List<(string? Titulo, IReadOnlyList<TrabajadorListaDto> Filas)>();
        string? claveAnterior = null;
        List<TrabajadorListaDto>? actual = null;
        foreach (var trabajador in _filas)
        {
            var (clave, nombre) = _agrupar == AgruparPorOficio
                ? (trabajador.OficioEmpresaId?.ToString(), trabajador.OficioNombre)
                : (trabajador.DelegacionEmpresaId?.ToString(), trabajador.DelegacionEmpresaNombre);
            if (actual is null || clave != claveAnterior)
            {
                actual = [];
                claveAnterior = clave;
                var titulo = nombre ?? Textos[_agrupar == AgruparPorOficio ? "SinOficio" : "SinDelegacionEmpresa"].Value;
                grupos.Add((titulo, actual));
            }

            actual.Add(trabajador);
        }

        return grupos;
    }

    private async Task CambiarFiltroAsync(FiltroTrabajadores filtro)
    {
        _filtro = filtro;
        _pagina = 1;
        _estadoDeLaUrl = FiltroEnUrl(filtro);
        NavigationManager.ActualizarFiltroEnUrl("estado", _estadoDeLaUrl);
        await MostrarPaginaAsync();
    }

    private async Task CambiarPestanaAsync(string pestana)
    {
        _pestana = pestana;
        _pestanaDeLaUrl = pestana == PestanaTrabajadores ? null : pestana;
        NavigationManager.ActualizarFiltroEnUrl("pestana", _pestanaDeLaUrl);
        await CargarClientesSiHaceFaltaAsync();
    }

    internal static FiltroTrabajadores FiltroDesdeUrl(string? valor) => valor switch
    {
        "sin-documentacion-valida" => FiltroTrabajadores.SinDocumentacionValida,
        "por-vencer" => FiltroTrabajadores.PorVencer,
        "al-dia" => FiltroTrabajadores.AlDia,
        _ => FiltroTrabajadores.Todos
    };

    internal static string? FiltroEnUrl(FiltroTrabajadores filtro) => filtro switch
    {
        FiltroTrabajadores.SinDocumentacionValida => "sin-documentacion-valida",
        FiltroTrabajadores.PorVencer => "por-vencer",
        FiltroTrabajadores.AlDia => "al-dia",
        _ => null
    };

    /// <summary>La pestaña Clientes, si al abrirla aún no hay lista (la carga de la página ya la pide).</summary>
    private async Task CargarClientesSiHaceFaltaAsync()
    {
        if (_pestana != PestanaClientes || _detalle is null || _clientes is not null || _errorClientes || _cargandoClientes)
            return;

        await CargarClientesAsync();
    }

    private ContactoAgendaDto? _contactoPrincipal;
    private int _cargaContacto;

    /// <summary>
    /// El contacto predeterminado de la Agenda de la Empresa, para la tarjeta «Se pide la documentación a».
    /// Lo lee la misma consulta que la pestaña Agenda, así que el alcance es el suyo. Un fallo, o que no haya
    /// contacto predeterminado, no tumba la página: la tarjeta simplemente no se pinta.
    /// </summary>
    private async Task CargarContactoPrincipalAsync()
    {
        var carga = ++_cargaContacto;
        var empresaId = EmpresaId;
        try
        {
            var contactos = await Mediator.Send(new ObtenerAgendaContactosQuery(TipoPropietarioAgenda.Empresa, empresaId), _cancelacion);
            if (carga == _cargaContacto)
                _contactoPrincipal = contactos.FirstOrDefault(c => c.EsPredeterminado);
        }
        catch (Exception)
        {
            if (carga == _cargaContacto)
                _contactoPrincipal = null;
        }
    }

    private Task IrAAgendaAsync() => CambiarPestanaAsync(PestanaAgenda);

    private async Task CargarClientesAsync()
    {
        var carga = ++_cargaClientes;
        var empresaId = EmpresaId;
        _cargandoClientes = true;
        _errorClientes = false;

        try
        {
            var clientes = await Mediator.Send(new ObtenerClientesDeEmpresaQuery(empresaId), _cancelacion);
            if (carga == _cargaClientes)
                _clientes = clientes;
        }
        catch (Exception)
        {
            if (carga == _cargaClientes)
                _errorClientes = true;
        }
        finally
        {
            if (carga == _cargaClientes)
                _cargandoClientes = false;
        }
    }

    /// <summary>
    /// Carpeta de documentación: solo la URL de la proyección existente.
    /// Las credenciales guardadas permanecen ocultas y no se pide la contraseña.
    /// Se conserva la puerta de autorización y de doble factor de la consulta.
    /// </summary>
    private async Task CargarAccesoAsync()
    {
        var carga = ++_cargaAcceso;
        var empresaId = EmpresaId;
        _cargandoAcceso = true;
        _errorAcceso = false;
        _accesoRequiereDosFactores = false;

        try
        {
            var credencial = await Mediator.Send(new ObtenerCredencialAccesoEmpresaSinContrasenaQuery(empresaId), _cancelacion);
            if (carga != _cargaAcceso) return;
            _credencial = credencial;
        }
        catch (SegundoFactorRequeridoParaCredencialesException)
        {
            if (carga == _cargaAcceso)
            {
                _credencial = null;
                _accesoRequiereDosFactores = true;
            }
        }
        catch (Exception)
        {
            if (carga == _cargaAcceso)
                _errorAcceso = true;
        }
        finally
        {
            if (carga == _cargaAcceso)
            {
                _cargandoAcceso = false;
                if (_pestana == PestanaFicha && !HayCajasDeFicha)
                    _pestana = PestanaTrabajadores;
                StateHasChanged();
            }
        }
    }

    public void Dispose()
    {
        InvalidarCargas();
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    private void IrABreadcrumb(int indice)
    {
        if (indice == 0)
            NavigationManager.NavigateTo("/empresas");
    }

    private Task AbrirInformacion() =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Empresa, EmpresaId, _detalle?.RazonSocial ?? string.Empty, "informacion");

    private Task AbrirTrabajador(TrabajadorListaDto trabajador) =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Trabajador, trabajador.Id, NombreCompleto(trabajador), "informacion");

    private Task AbrirCliente(ClienteDeEmpresaDto cliente) =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Cliente, cliente.Id, cliente.RazonSocial, "informacion");

    private static string NombreCompleto(TrabajadorListaDto trabajador) => $"{trabajador.Nombre} {trabajador.Apellidos}".Trim();

    // Avatar de la fila: las dos primeras palabras del nombre (mockup, t.ini), como MiTrabajo.
    private static string Iniciales(string nombre) => string.Concat(nombre
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(p => char.IsLetter(p[0]))
        .Take(2)
        .Select(p => char.ToUpperInvariant(p[0])));

    private string Plural(int n, string claveUno, string claveVarios) =>
        n == 1 ? Textos[claveUno] : Textos[claveVarios, n];

    private IReadOnlyList<PestanaDefinicion> Pestanas =>
    [
        new(PestanaTrabajadores, Textos["PestanaTrabajadores"])
        {
            Contador = _recuentos is { } r ? new ContadorPestana(r.Total, Textos["GlosaTrabajadores"]) : null
        },
        new(PestanaClientes, Textos["PestanaClientes"])
        {
            Contador = _clientes is { Count: > 0 } clientes ? new ContadorPestana(clientes.Count, Textos["GlosaClientes"]) : null
        },
        new(PestanaDocumentacion, Textos["PestanaDocumentacion"]),
        new(PestanaAgenda, Textos["PestanaAgenda"]),
        .. HayCajasDeFicha ? new[] { new PestanaDefinicion(PestanaFicha, Textos["PestanaFicha"]) } : [],
        new(PestanaHistorial, Textos["PestanaHistorial"])
    ];

    // ---------- Nota interna (mismo patrón que Subcontrata 360) ----------

    private bool _editorNotaVisible;
    private bool _guardandoNota;
    private string _notaEnEdicion = string.Empty;
    /// <summary>Lo que el usuario escribió cuando otra persona guardó antes: se recupera al reabrir el editor, nunca se reenvía solo.</summary>
    private string? _borradorNotaTrasConflicto;
    private string? _mensajeErrorNota;
    private string? _errorCampoNota;
    private readonly InstantaneaFormulario _instantaneaNota = new();

    private bool HayCambiosEnLaNota => _editorNotaVisible && _instantaneaNota.Difiere(_notaEnEdicion);

    private void AbrirEditorNota()
    {
        if (_detalle is null) return;

        _notaEnEdicion = _borradorNotaTrasConflicto ?? _detalle.Notas ?? string.Empty;
        _borradorNotaTrasConflicto = null;
        _mensajeErrorNota = null;
        _errorCampoNota = null;
        _instantaneaNota.Fijar(_notaEnEdicion);
        _editorNotaVisible = true;
    }

    private void CambiarVisibilidadEditorNota(bool visible) => _editorNotaVisible = visible;

    /// <summary>
    /// Manda solo la nota y la <c>Version</c> que la ficha leyó. Ante un conflicto el editor se cierra, se relee la
    /// cabecera (la tarjeta enseña la nota de la otra persona) y el texto propio queda como borrador al reabrir.
    /// </summary>
    private async Task GuardarNotaAsync()
    {
        if (_detalle is null || _guardandoNota) return;

        var generacion = _generacion;
        var detalle = _detalle;
        _guardandoNota = true;
        _mensajeErrorNota = null;
        _errorCampoNota = null;

        try
        {
            var resultado = await Mediator.Send(new GuardarNotaInternaEmpresaCommand(detalle.Id, _notaEnEdicion, detalle.Version));

            if (resultado.EsFallido)
            {
                if (generacion != _generacion) return;
                if (resultado.Error.Codigo == ConcurrenciaOptimista.CodigoConflicto)
                {
                    _borradorNotaTrasConflicto = _notaEnEdicion;
                    ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                    _editorNotaVisible = false;
                    await RecargarDetalleTrasNotaAsync(generacion);
                }
                else
                {
                    _mensajeErrorNota = resultado.Error.Mensaje;
                }
                return;
            }

            ToastService.Mostrar(Textos["ToastNotaInternaGuardada"], TonoToast.Exito);
            if (generacion != _generacion) return;
            _editorNotaVisible = false;

            // La tarjeta enseña ya lo guardado aunque la relectura falle; la Version buena la trae la relectura.
            _detalle = detalle with { Notas = string.IsNullOrWhiteSpace(_notaEnEdicion) ? null : _notaEnEdicion.Trim() };
            await RecargarDetalleTrasNotaAsync(generacion);
        }
        catch (FluentValidation.ValidationException ex)
        {
            if (generacion == _generacion)
                _errorCampoNota = ex.Errors.FirstOrDefault()?.ErrorMessage ?? Textos["ErrorGuardarNotaInterna"];
        }
        catch (Exception)
        {
            if (generacion == _generacion)
                _mensajeErrorNota = Textos["ErrorGuardarNotaInterna"];
        }
        finally
        {
            if (generacion == _generacion)
                _guardandoNota = false;
        }
    }

    private Task FichaGuardadaAsync() => RecargarDetalleTrasNotaAsync(_generacion);

    /// <summary>Relee solo la cabecera (nota y Version). Si falla, la ficha se queda con lo que ya enseñaba.</summary>
    private async Task RecargarDetalleTrasNotaAsync(int generacion)
    {
        try
        {
            var detalle = await Mediator.Send(new ObtenerEmpresaPorIdQuery(EmpresaId), _cancelacion);
            if (generacion == _generacion && detalle is not null && detalle.Id == _detalle?.Id)
                _detalle = detalle;
        }
        catch (Exception)
        {
            // Sin relectura la Version queda vieja: el siguiente guardado dará conflicto y releerá.
        }
    }
}

/// <summary>Chips de la pestaña Trabajadores de Empresa 360.</summary>
public enum FiltroTrabajadores
{
    Todos,

    /// <summary>Peor documento vencido (el handler no produce Faltante: ver <see cref="EmpresaDetalle"/>).</summary>
    SinDocumentacionValida,

    /// <summary>Peor documento urgente o próximo.</summary>
    PorVencer,

    /// <summary>El resto: peor documento vigente, o ninguno con fecha de vencimiento.</summary>
    AlDia
}

/// <summary>
/// Trabajadores de una Empresa por el peor estado de sus documentos. Los
/// cuatro grupos parten el total: cada trabajador está en uno solo.
/// </summary>
public sealed record RecuentoTrabajadores(int Total, int Vencidos, int Urgentes, int Proximos)
{
    public int PorVencer => Urgentes + Proximos;
    public int AlDia => Math.Max(0, Total - Vencidos - Urgentes - Proximos);
}
