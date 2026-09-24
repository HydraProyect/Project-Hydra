using CaeManager.Application.Empresas.Queries.ObtenerClientesDeEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerCredencialAccesoEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerCredencialAccesoEmpresaSinContrasena;
using CaeManager.Application.Empresas.Queries.ObtenerCumplimientoEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Empresas.Pages;

/// <summary>
/// Empresa 360 (decisión del propietario 2026-09-22: cada 360 tiene página
/// propia además del panel de 520 px). Compone, con las MISMAS consultas que
/// <c>EmpresaWorkspacePanel</c> —mismo alcance (<c>IAlcanceDatosService</c>) y
/// misma RLS—, la cabecera de cumplimiento, la lista de trabajadores de la
/// Empresa y, como pestañas, los mismos componentes que el panel
/// (Documentación, Agenda, Sello, Historial). La edición de la identidad y de
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
public partial class EmpresaDetalle : ComponentBase, IDisposable
{
    internal const string PestanaTrabajadores = "trabajadores";
    internal const string PestanaClientes = "clientes";
    internal const string PestanaDocumentacion = "documentacion";
    internal const string PestanaAgenda = "agenda";
    internal const string PestanaSello = "sello";
    internal const string PestanaHistorial = "historial";

    internal const int TamanoPagina = 20;

    private static readonly string[] PestanasValidas =
        [PestanaTrabajadores, PestanaClientes, PestanaDocumentacion, PestanaAgenda, PestanaSello, PestanaHistorial];

    [Parameter] public Guid EmpresaId { get; set; }

    /// <summary>Pestaña activa, en la URL (sin parámetro = Trabajadores), como los filtros de Centro 360.</summary>
    [Parameter, SupplyParameterFromQuery(Name = "pestana")] public string? Pestana { get; set; }

    /// <summary>Chip de estado de la pestaña Trabajadores, en la URL (sin parámetro = Todos).</summary>
    [Parameter, SupplyParameterFromQuery(Name = "estado")] public string? Estado { get; set; }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ContextWorkspaceService WorkspaceService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private EmpresaDetalleDto? _detalle;
    private int? _cumplimiento;
    private bool _cargando = true;
    private bool _error;

    private RecuentoTrabajadores? _recuentos;
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

    private CredencialAccesoEmpresaSinContrasenaDto? _credencial;
    private bool _cargandoAcceso = true;
    private bool _errorAcceso;

    private string _pestana = PestanaTrabajadores;
    private FiltroTrabajadores _filtro = FiltroTrabajadores.Todos;
    private string? _pestanaDeLaUrl;
    private string? _estadoDeLaUrl;
    private bool _urlAdoptada;

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
        var filtroCambio = AdoptarUrl();

        if (_empresaCargada != EmpresaId)
        {
            _empresaCargada = EmpresaId;
            ReiniciarParaNuevaEmpresa();
            await CargarAsync();
            return;
        }

        if (filtroCambio && _recuentos is not null)
        {
            _pagina = 1;
            await MostrarPaginaAsync();
        }

        await CargarClientesSiHaceFaltaAsync();
    }

    /// <summary>
    /// Adopta pestaña y chip cuando cambian en la URL AHÍ FUERA —un enlace, el
    /// botón «atrás»—; lo que cambia la propia página ya lo apuntó antes de
    /// navegar. Devuelve si el chip cambió.
    /// </summary>
    private bool AdoptarUrl()
    {
        var filtroCambio = false;
        if (!_urlAdoptada || !string.Equals(_pestanaDeLaUrl, Pestana, StringComparison.Ordinal))
        {
            _pestanaDeLaUrl = Pestana;
            _pestana = PestanasValidas.Contains(Pestana) ? Pestana! : PestanaTrabajadores;
        }

        if (!_urlAdoptada || !string.Equals(_estadoDeLaUrl, Estado, StringComparison.Ordinal))
        {
            _estadoDeLaUrl = Estado;
            var filtro = FiltroDesdeUrl(Estado);
            filtroCambio = filtro != _filtro;
            _filtro = filtro;
        }

        _urlAdoptada = true;
        return filtroCambio;
    }

    private void InvalidarCargas()
    {
        _generacion++;
        _cargaDetalle++;
        _cargaTrabajadores++;
        _cargaPagina++;
        _cargaClientes++;
        _cargaAcceso++;
    }

    /// <summary>Nada de la Empresa anterior sobrevive al cambio.</summary>
    private void ReiniciarParaNuevaEmpresa()
    {
        InvalidarCargas();
        _detalle = null;
        _cumplimiento = null;
        _error = false;
        _recuentos = null;
        _filas = [];
        _paginasOrdenadas.Clear();
        _cargandoTrabajadores = true;
        _errorTrabajadores = false;
        _pagina = 1;
        _clientes = null;
        _cargandoClientes = false;
        _errorClientes = false;
        _credencial = null;
        _cargandoAcceso = true;
        _errorAcceso = false;
    }

    private async Task CargarAsync()
    {
        var carga = ++_cargaDetalle;
        var empresaId = EmpresaId;
        _cargando = true;
        _error = false;

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
                _error = true;
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

        await CargarTrabajadoresAsync();
        // Los Clientes empresariales se piden siempre, no solo al abrir la pestaña:
        // de esta lista acotada por alcance sale el recuento de la cabecera y del
        // contador (EmpresaDetalleDto.ClienteIds no está acotado).
        await CargarClientesAsync();

        // Mismo motivo que CentroDetalle: OnParametersSetAsync también corre
        // en el prerenderizado estático, y una tarea sin await ahí queda en
        // vuelo cuando ASP.NET Core ya liberó el scope de DI.
        if (RendererInfo.IsInteractive && carga == _cargaDetalle)
            _ = CargarAccesoAsync();
    }

    /// <summary>
    /// Recuentos por estado y la página visible. Un fallo aquí no tumba la
    /// página: la Empresa sí cargó, y la pestaña enseña su propio error.
    /// </summary>
    private async Task CargarTrabajadoresAsync()
    {
        var carga = ++_cargaTrabajadores;
        var empresaId = EmpresaId;
        _cargandoTrabajadores = true;
        _errorTrabajadores = false;
        _paginasOrdenadas.Clear();

        try
        {
            var primera = await Mediator.Send(ConsultaOrdenada(empresaId, 1), _cancelacion);
            if (carga != _cargaTrabajadores) return;
            var vencidos = await ContarAsync(empresaId, EstadoDocumento.Vencido);
            if (carga != _cargaTrabajadores) return;
            var urgentes = await ContarAsync(empresaId, EstadoDocumento.Urgente);
            if (carga != _cargaTrabajadores) return;
            var proximos = await ContarAsync(empresaId, EstadoDocumento.Proximo);
            if (carga != _cargaTrabajadores) return;

            _paginasOrdenadas[1] = primera.Elementos;
            _recuentos = new RecuentoTrabajadores(primera.TotalElementos, vencidos, urgentes, proximos);
            await MostrarPaginaAsync();
        }
        catch (Exception)
        {
            if (carga == _cargaTrabajadores)
            {
                _recuentos = null;
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
    private async Task<int> ContarAsync(Guid empresaId, EstadoDocumento estado)
    {
        var resultado = await Mediator.Send(new ObtenerTrabajadoresQuery(
            null, EmpresaId: empresaId, Pagina: 1, TamanoPagina: 1, EstadoDocumental: estado.ToString()), _cancelacion);
        return resultado.TotalElementos;
    }

    private static ObtenerTrabajadoresQuery ConsultaOrdenada(Guid empresaId, int pagina) =>
        new(null, EmpresaId: empresaId, Pagina: pagina, TamanoPagina: TamanoPagina,
            OrdenarPor: nameof(TrabajadorListaDto.EstadoDocumental));

    /// <summary>
    /// Pinta la página <see cref="_pagina"/> del chip activo: su tramo de la
    /// lista ordenada, pedido en páginas de esa lista (las ya pedidas no se
    /// repiten). Las filas se filtran además por su estado: si los documentos
    /// cambian entre el recuento y la página, sobra alguna fila en vez de
    /// colarse una de otro estado.
    /// </summary>
    private async Task MostrarPaginaAsync()
    {
        if (_recuentos is not { } recuentos) return;

        var carga = ++_cargaPagina;
        var generacion = _generacion;
        var empresaId = EmpresaId;
        var filtro = _filtro;
        var (desde, cantidad) = Tramo(recuentos, filtro, _pagina, TamanoPagina);
        if (cantidad <= 0)
        {
            _filas = [];
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
                    var resultado = await Mediator.Send(ConsultaOrdenada(empresaId, pagina), _cancelacion);
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

    internal static bool Incluye(FiltroTrabajadores filtro, EstadoDocumento? estado) => filtro switch
    {
        FiltroTrabajadores.SinDocumentacionValida => estado is EstadoDocumento.Vencido or EstadoDocumento.Faltante,
        FiltroTrabajadores.PorVencer => estado is EstadoDocumento.Urgente or EstadoDocumento.Proximo,
        FiltroTrabajadores.AlDia => estado is null or EstadoDocumento.Vigente or EstadoDocumento.SinCaducidad,
        _ => true
    };

    private int TotalDelFiltro => _recuentos is not { } r ? 0 : _filtro switch
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

    private async Task CambiarFiltroAsync(FiltroTrabajadores filtro)
    {
        _filtro = filtro;
        _pagina = 1;
        _estadoDeLaUrl = FiltroEnUrl(filtro);
        NavigationManager.ActualizarFiltroEnUrl("estado", _estadoDeLaUrl);
        await MostrarPaginaAsync();
    }

    /// <summary>
    /// Pulsar un indicador de la cabecera lleva a la pestaña Trabajadores con
    /// su chip puesto: pestaña y chip en una sola navegación.
    /// </summary>
    private async Task FiltrarDesdeIndicadorAsync(FiltroTrabajadores filtro)
    {
        _pestana = PestanaTrabajadores;
        _pestanaDeLaUrl = null;
        _filtro = filtro;
        _pagina = 1;
        _estadoDeLaUrl = FiltroEnUrl(filtro);
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["pestana"] = null,
            ["estado"] = _estadoDeLaUrl
        });
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
    /// Tarjeta «Acceso a plataforma CAE»: los campos NO sensibles de la
    /// credencial (la proyección ni lee la contraseña). Aparte del resto, como
    /// los canales de Centro 360: es contexto del lateral.
    /// </summary>
    private async Task CargarAccesoAsync()
    {
        var carga = ++_cargaAcceso;
        var empresaId = EmpresaId;
        _cargandoAcceso = true;
        _errorAcceso = false;

        try
        {
            var credencial = await Mediator.Send(new ObtenerCredencialAccesoEmpresaSinContrasenaQuery(empresaId), _cancelacion);
            if (carga != _cargaAcceso) return;
            _credencial = credencial;
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
                StateHasChanged();
            }
        }
    }

    /// <summary>
    /// «Copiar contraseña» (DEC-53/DEC-62, decisión del propietario
    /// 2026-09-21): la contraseña se pide al servidor solo en el clic
    /// explícito, nunca al abrir la página, y no se pinta: va del servidor al
    /// portapapeles. <see cref="ObtenerCredencialAccesoEmpresaQuery"/> es la
    /// única vía que la descifra, exactamente como en el panel.
    /// </summary>
    private async Task<string?> CopiarContrasenaAsync()
    {
        var empresaId = EmpresaId;
        var credencial = await Mediator.Send(new ObtenerCredencialAccesoEmpresaQuery(empresaId), _cancelacion);
        if (empresaId != EmpresaId) return null;

        if (string.IsNullOrEmpty(credencial?.Contrasena))
        {
            ToastService.Mostrar(Textos["SinContrasena"], TonoToast.Info);
            return null;
        }

        return credencial.Contrasena;
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
        new(PestanaSello, Textos["PestanaSello"]),
        new(PestanaHistorial, Textos["PestanaHistorial"])
    ];
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
