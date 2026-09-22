using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerEmpresasDeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerResumenCliente;
using CaeManager.Application.Clientes.Queries.ObtenerSubcontratasDeCliente;
using CaeManager.Domain.Centros;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Clientes.Pages;

/// <summary>
/// Cliente 360, página (<c>/clientes/{id}</c>): la vista completa del Cliente
/// empresarial, junto al panel de 520 px (<c>ClienteWorkspacePanel</c>) que se
/// sigue abriendo para consultar de un vistazo y para operar. La ruta convive
/// con <c>/clientes/{id}/lectura-ia</c>, que es más específica.
///
/// <para>
/// <b>Mismas consultas y mismo alcance que el panel.</b> Cabecera
/// (<see cref="ObtenerClientePorIdQuery"/>), recuentos
/// (<see cref="ObtenerResumenClienteQuery"/>), empresas y subcontratas: las
/// cuatro del panel, que filtran por <c>IAlcanceDatosService</c> y viajan con
/// RLS. Centros NO usa <c>ObtenerCentrosDeClienteQuery</c> del panel (solo trae
/// nombre y Empresa), sino <see cref="ObtenerCentrosQuery"/> con
/// <c>ClienteId</c> —la misma que la lista /centros, con el mismo alcance
/// (<c>ObtenerCentroIdsVisiblesAsync</c>)—, porque es la que trae por Centro
/// su <see cref="EstadoCentro"/>, su porcentaje y sus recuentos.
/// </para>
///
/// <para>
/// <b>Todos los centros del cliente, sin query nueva.</b> Se piden ordenados
/// por Estado descendente (del peor al mejor): ese orden obliga al handler a
/// tomar su «camino con estado», que ya materializa y calcula el estado de
/// TODOS los centros del cliente antes de paginar — el coste es el de todos
/// sus centros sea cual sea el tamaño de página. El tamaño de página
/// (<see cref="MaximoCentros"/>) solo acota lo que se pinta; si el cliente
/// tiene más, la lista lo dice y los indicadores se cuentan sobre lo pintado.
/// </para>
///
/// <para>
/// Qué NO enseña, igual que el panel: porcentaje de cumplimiento del Cliente
/// empresarial (no existe; por eso no hay anillo), Alias, Domicilio, Portal
/// principal, Gestor CAE e historial de notas. Editar la identidad y la nota
/// se hace en el panel: los «Editar» de esta página lo abren en su pestaña.
/// </para>
/// </summary>
public partial class ClienteDetalle : ComponentBase, IDisposable
{
    /// <summary>Tope de centros pintados. El coste de la consulta no depende de él (ver la clase).</summary>
    internal const int MaximoCentros = 200;

    internal const string PestanaPorDefecto = "centros";

    /// <summary>
    /// Orden de la tira: Centros primera y por defecto porque es la única que
    /// trae estado; el resto sigue el orden del panel. «Actividad» del panel se
    /// rotula «Historial», como en los demás 360 (mismo PestanaHistorial).
    /// </summary>
    private static readonly (string Id, string Clave)[] _pestanas =
    [
        ("centros", "PestanaCentros"),
        ("empresas", "PestanaEmpresas"),
        ("subcontratas", "PestanaSubcontratas"),
        ("blindaje42", "PestanaBlindaje"),
        ("documentacion", "PestanaDocumentacion"),
        ("agenda", "PestanaAgenda"),
        ("historial", "PestanaHistorial")
    ];

    [Parameter] public Guid ClienteId { get; set; }

    /// <summary>
    /// Pestaña activa, en la URL (<c>?pestana=empresas</c>; sin parámetro,
    /// Centros), como los filtros de Centro 360: se puede enlazar y recargar
    /// sin perder dónde se estaba. Un valor desconocido cae a Centros.
    /// </summary>
    [Parameter, SupplyParameterFromQuery(Name = "pestana")] public string? Pestana { get; set; }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ContextWorkspaceService WorkspaceService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private ClienteDetalleDto? _detalle;
    private bool _cargando = true;
    private bool _error;

    /// <summary>Si su consulta falla se queda en null y la entradilla omite los recuentos: un fallo no se disfraza de «0 centros».</summary>
    private ResumenClienteDto? _resumen;

    private IReadOnlyList<CentroListaDto>? _centros;
    private int _totalCentros;
    private bool _cargandoCentros;
    private bool _errorCentros;
    private IReadOnlyList<EmpresaDeClienteDto>? _empresas;
    private bool _cargandoEmpresas;
    private bool _errorEmpresas;
    private IReadOnlyList<SubcontrataDeClienteDto>? _subcontratas;
    private bool _cargandoSubcontratas;
    private bool _errorSubcontratas;

    private string _pestanaActiva = PestanaPorDefecto;
    private string? _pestanaDeLaUrl;

    /// <summary>
    /// Cliente que esta instancia tiene cargado. Blazor reutiliza la instancia
    /// al pasar de un cliente a otro, y cambiar de pestaña también llega por
    /// <see cref="OnParametersSetAsync"/> (viaja en la URL): sin esto, cada
    /// cambio de pestaña relanzaría la carga entera.
    /// </summary>
    private Guid? _clienteCargado;

    /// <summary>
    /// Generación del cliente que se enseña y un contador por carga. Cambiar de
    /// cliente y retirar la página los suben: cada respuesta compara el suyo
    /// antes de escribir y, si ya no es el vigente, se descarta.
    /// </summary>
    private int _generacion;
    private int _cargaCabecera;
    private int _cargaResumen;
    private int _cargaCentros;
    private int _cargaEmpresas;
    private int _cargaSubcontratas;

    /// <summary>
    /// Los contadores impiden que una respuesta tardía escriba; esto corta la
    /// consulta misma. El token se copia al inicializar porque leer
    /// <c>Token</c> de un <see cref="CancellationTokenSource"/> ya desechado lanza.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private CancellationToken _cancelacion;

    private IReadOnlyList<BreadcrumbElemento> Miguero =>
        [new BreadcrumbElemento(Textos["MigaClientes"]), new BreadcrumbElemento(_detalle?.RazonSocial ?? "…")];

    protected override void OnInitialized() => _cancelacion = _ciclo.Token;

    protected override async Task OnParametersSetAsync()
    {
        AdoptarPestanaDeLaUrl();

        if (_clienteCargado == ClienteId)
            return;

        _clienteCargado = ClienteId;
        ReiniciarParaNuevoCliente();
        await CargarTodoAsync();
    }

    /// <summary>
    /// La pestaña de la URL se adopta cuando cambia AHÍ FUERA —un deep link, el
    /// botón «atrás»—, no en cada repintado: el clic ya la puso en local antes
    /// de que la URL la confirme.
    /// </summary>
    private void AdoptarPestanaDeLaUrl()
    {
        if (string.Equals(_pestanaDeLaUrl, Pestana, StringComparison.Ordinal))
            return;

        _pestanaDeLaUrl = Pestana;
        _pestanaActiva = Normalizar(Pestana);
    }

    private static string Normalizar(string? pestana) =>
        _pestanas.Any(p => p.Id == pestana) ? pestana! : PestanaPorDefecto;

    private void CambiarPestana(string pestana)
    {
        _pestanaActiva = Normalizar(pestana);
        _pestanaDeLaUrl = _pestanaActiva == PestanaPorDefecto ? null : _pestanaActiva;
        NavigationManager.ActualizarFiltroEnUrl("pestana", _pestanaDeLaUrl);
    }

    /// <summary>Los indicadores de la cabecera cuentan centros: al pulsarlos se ven esos centros.</summary>
    private void IrACentros() => CambiarPestana(PestanaPorDefecto);

    /// <summary>Invalida cualquier carga en vuelo: su respuesta ya no escribirá nada.</summary>
    private void InvalidarCargas()
    {
        _generacion++;
        _cargaCabecera++;
        _cargaResumen++;
        _cargaCentros++;
        _cargaEmpresas++;
        _cargaSubcontratas++;
    }

    private void ReiniciarParaNuevoCliente()
    {
        InvalidarCargas();
        _detalle = null;
        _error = false;
        _resumen = null;
        _centros = null;
        _totalCentros = 0;
        _errorCentros = false;
        _cargandoCentros = false;
        _empresas = null;
        _errorEmpresas = false;
        _cargandoEmpresas = false;
        _subcontratas = null;
        _errorSubcontratas = false;
        _cargandoSubcontratas = false;
    }

    /// <summary>
    /// Cabecera y, detrás, lo que alimenta sus recuentos, sus indicadores y las
    /// pestañas, en serie: todas las consultas comparten el DbContext del
    /// circuito. Cada paso pinta en cuanto llega.
    /// </summary>
    private async Task CargarTodoAsync()
    {
        var generacion = _generacion;
        await CargarCabeceraAsync();
        if (generacion != _generacion || _detalle is null) return;

        _cargandoCentros = true;
        _cargandoEmpresas = true;
        _cargandoSubcontratas = true;
        StateHasChanged();

        await CargarResumenAsync();
        if (generacion != _generacion) return;
        await CargarCentrosAsync();
        if (generacion != _generacion) return;
        await CargarEmpresasAsync();
        if (generacion != _generacion) return;
        await CargarSubcontratasAsync();
    }

    /// <summary>Un doble clic en «Reintentar» no lanza una segunda cadena: la primera ya puso _cargando.</summary>
    private Task ReintentarAsync() => _cargando ? Task.CompletedTask : CargarTodoAsync();

    private async Task CargarCabeceraAsync()
    {
        var carga = ++_cargaCabecera;
        var id = ClienteId;
        _cargando = true;
        _error = false;

        try
        {
            var detalle = await Mediator.Send(new ObtenerClientePorIdQuery(id), _cancelacion);
            if (carga != _cargaCabecera) return;
            _detalle = detalle;
            _error = detalle is null;
        }
        catch (Exception)
        {
            if (carga == _cargaCabecera)
                _error = true;
        }
        finally
        {
            if (carga == _cargaCabecera)
                _cargando = false;
        }
    }

    private async Task CargarResumenAsync()
    {
        var carga = ++_cargaResumen;
        var id = ClienteId;

        try
        {
            var resumen = await Mediator.Send(new ObtenerResumenClienteQuery(id), _cancelacion);
            if (carga != _cargaResumen) return;
            _resumen = resumen;
        }
        catch (Exception)
        {
            // Se queda en null a propósito: la entradilla omite los recuentos.
        }
        finally
        {
            if (carga == _cargaResumen)
                StateHasChanged();
        }
    }

    private Task ReintentarCentrosAsync() => _cargandoCentros ? Task.CompletedTask : CargarCentrosAsync();

    private async Task CargarCentrosAsync()
    {
        var carga = ++_cargaCentros;
        var id = ClienteId;
        _cargandoCentros = true;
        _errorCentros = false;

        try
        {
            var pagina = await Mediator.Send(new ObtenerCentrosQuery(
                Busqueda: null, ClienteId: id,
                OrdenarPor: nameof(CentroListaDto.Estado), Descendente: true,
                Pagina: 1, TamanoPagina: MaximoCentros), _cancelacion);
            if (carga != _cargaCentros) return;
            _centros = pagina.Elementos;
            _totalCentros = pagina.TotalElementos;
        }
        catch (Exception)
        {
            if (carga == _cargaCentros)
                _errorCentros = true;
        }
        finally
        {
            if (carga == _cargaCentros)
            {
                _cargandoCentros = false;
                StateHasChanged();
            }
        }
    }

    private Task ReintentarEmpresasAsync() => _cargandoEmpresas ? Task.CompletedTask : CargarEmpresasAsync();

    private async Task CargarEmpresasAsync()
    {
        var carga = ++_cargaEmpresas;
        var id = ClienteId;
        _cargandoEmpresas = true;
        _errorEmpresas = false;

        try
        {
            var empresas = await Mediator.Send(new ObtenerEmpresasDeClienteQuery(id), _cancelacion);
            if (carga != _cargaEmpresas) return;
            _empresas = empresas;
        }
        catch (Exception)
        {
            if (carga == _cargaEmpresas)
                _errorEmpresas = true;
        }
        finally
        {
            if (carga == _cargaEmpresas)
            {
                _cargandoEmpresas = false;
                StateHasChanged();
            }
        }
    }

    private Task ReintentarSubcontratasAsync() => _cargandoSubcontratas ? Task.CompletedTask : CargarSubcontratasAsync();

    private async Task CargarSubcontratasAsync()
    {
        var carga = ++_cargaSubcontratas;
        var id = ClienteId;
        _cargandoSubcontratas = true;
        _errorSubcontratas = false;

        try
        {
            var subcontratas = await Mediator.Send(new ObtenerSubcontratasDeClienteQuery(id), _cancelacion);
            if (carga != _cargaSubcontratas) return;
            _subcontratas = subcontratas;
        }
        catch (Exception)
        {
            if (carga == _cargaSubcontratas)
                _errorSubcontratas = true;
        }
        finally
        {
            if (carga == _cargaSubcontratas)
            {
                _cargandoSubcontratas = false;
                StateHasChanged();
            }
        }
    }

    /// <summary>
    /// Al navegar fuera se invalida lo que estuviera en vuelo y se cancela la
    /// consulta emitida; la cancelación llega como una excepción que el catch
    /// de cada carga descarta, porque su contador dejó de ser el vigente.
    /// </summary>
    public void Dispose()
    {
        InvalidarCargas();
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    // ── Textos derivados ──────────────────────────────────────────────────

    private string Plural(int n, string claveUno, string claveVarios) =>
        n == 1 ? Textos[claveUno] : Textos[claveVarios, n];

    /// <summary>«3 centros · 42 trabajadores» (ResumenClienteDto). Sin cumplimiento: no existe por Cliente empresarial.</summary>
    private string? TextoRecuentos =>
        _resumen is null
            ? null
            : $"{Plural(_resumen.TotalCentros, "RecuentoCentrosUno", "RecuentoCentrosVarios")} · " +
              Plural(_resumen.TotalTrabajadores, "RecuentoTrabajadoresUno", "RecuentoTrabajadoresVarios");

    private string ResumenCentros
    {
        get
        {
            var mostrados = _centros?.Count ?? 0;
            return _totalCentros > mostrados
                ? Textos["ResumenCentrosTruncado", mostrados, _totalCentros]
                : Plural(mostrados, "ResumenCentrosUno", "ResumenCentrosVarios");
        }
    }

    /// <summary>«Empresa: Ibertec GmbH · 3 vencidos · 1 próximo», sin partes en cero.</summary>
    private string DetalleCentro(CentroListaDto centro)
    {
        var partes = new List<string> { Textos["DetalleEmpresa", centro.EmpresaRazonSocial] };
        if (centro.Recuentos.TotalVencidas > 0)
            partes.Add(Plural(centro.Recuentos.TotalVencidas, "DetalleVencidosUno", "DetalleVencidosVarios"));
        if (centro.Recuentos.TotalProximas > 0)
            partes.Add(Plural(centro.Recuentos.TotalProximas, "DetalleProximosUno", "DetalleProximosVarios"));
        return string.Join(" · ", partes);
    }

    private IReadOnlyList<PestanaDefinicion> PestanasConRecuento =>
        _pestanas.Select(p =>
        {
            var definicion = new PestanaDefinicion(p.Id, Textos[p.Clave]);
            return p.Id switch
            {
                "centros" when _centros is not null =>
                    definicion with { Contador = new ContadorPestana(_totalCentros, Plural(_totalCentros, "GlosaCentroUno", "GlosaCentroVarios")) },
                "empresas" when _empresas is not null =>
                    definicion with { Contador = new ContadorPestana(_empresas.Count, Plural(_empresas.Count, "GlosaEmpresaUno", "GlosaEmpresaVarios")) },
                "subcontratas" when _subcontratas is not null =>
                    definicion with { Contador = new ContadorPestana(_subcontratas.Count, Plural(_subcontratas.Count, "GlosaSubcontrataUno", "GlosaSubcontrataVarios")) },
                _ => definicion
            };
        }).ToList();

    /// <summary>Un indicador de la cabecera y el desglose literal de su ventana de contexto.</summary>
    private sealed record Indicador(
        string Clave, string Texto, string Titulo, string Etiqueta, IReadOnlyList<string> Lineas,
        TonoBadge Tono, TamanoBadge Tamano);

    /// <summary>
    /// Los centros de este cliente, no documentos sumados: un documento de
    /// Empresa exigido en varios centros no se cuenta varias veces. Un
    /// indicador en cero no se pinta. «Vencidos» incluye Faltante, como en
    /// RecuentosCentroDto.
    /// </summary>
    private IReadOnlyList<Indicador> Indicadores
    {
        get
        {
            if (_centros is null) return [];

            var indicadores = new List<Indicador>();

            var bloqueados = _centros.Where(c => c.Estado == EstadoCentro.Bloqueado).ToList();
            if (bloqueados.Count > 0)
            {
                var texto = _totalCentros == 1
                    ? Textos["IndicadorBloqueadosUnico"]
                    : Textos["IndicadorBloqueados", bloqueados.Count, _totalCentros];
                indicadores.Add(Crear("bloqueados", texto,
                    Plural(bloqueados.Count, "VentanaBloqueadosUno", "VentanaBloqueadosVarios"),
                    bloqueados.Select(c => $"{c.Nombre} · {c.EmpresaRazonSocial}").ToList(),
                    TonoBadge.Peligro, TamanoBadge.Medio));
            }

            var conVencidos = _centros.Where(c => c.Recuentos.TotalVencidas > 0).ToList();
            if (conVencidos.Count > 0)
            {
                indicadores.Add(Crear("vencidos",
                    Plural(conVencidos.Count, "IndicadorVencidosUno", "IndicadorVencidosVarios"),
                    Plural(conVencidos.Count, "VentanaVencidosUno", "VentanaVencidosVarios"),
                    conVencidos.Select(c => $"{c.Nombre} · {Plural(c.Recuentos.TotalVencidas, "DetalleVencidosUno", "DetalleVencidosVarios")}").ToList(),
                    TonoBadge.Peligro, TamanoBadge.Pequeno));
            }

            var conProximos = _centros.Where(c => c.Recuentos.TotalProximas > 0).ToList();
            if (conProximos.Count > 0)
            {
                indicadores.Add(Crear("proximos",
                    Plural(conProximos.Count, "IndicadorProximosUno", "IndicadorProximosVarios"),
                    Plural(conProximos.Count, "VentanaProximosUno", "VentanaProximosVarios"),
                    conProximos.Select(c => $"{c.Nombre} · {Plural(c.Recuentos.TotalProximas, "DetalleProximosUno", "DetalleProximosVarios")}").ToList(),
                    TonoBadge.Advertencia, TamanoBadge.Pequeno));
            }

            return indicadores;
        }
    }

    private Indicador Crear(string clave, string texto, string titulo, IReadOnlyList<string> lineas, TonoBadge tono, TamanoBadge tamano) =>
        new(clave, texto, titulo, Textos["AriaIndicador", texto, string.Join("; ", lineas)], lineas, tono, tamano);

    // ── Navegación ────────────────────────────────────────────────────────

    private void IrABreadcrumb(int indice)
    {
        if (indice == 0)
            NavigationManager.NavigateTo("/clientes");
    }

    private Task AbrirInformacionAsync() =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Cliente, ClienteId, _detalle?.RazonSocial ?? string.Empty, "informacion");

    private Task AbrirNotasAsync() =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Cliente, ClienteId, _detalle?.RazonSocial ?? string.Empty, "notas");

    private Task AbrirCentroAsync(CentroListaDto centro) =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Centro, centro.Id, centro.Nombre, "informacion");

    private Task AbrirEmpresaAsync(EmpresaDeClienteDto empresa) =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Empresa, empresa.Id, empresa.RazonSocial, "informacion");

    private Task AbrirSubcontrataAsync(SubcontrataDeClienteDto subcontrata) =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Subcontrata, subcontrata.Id, subcontrata.RazonSocial, "informacion");
}
