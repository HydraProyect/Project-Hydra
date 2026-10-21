using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionExigidaDeCliente;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerEmpresasDeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerResumenCliente;
using CaeManager.Application.Clientes.Queries.ObtenerSubcontratasDeCliente;
using CaeManager.Application.Clientes.Commands.EliminarCliente;
using CaeManager.Application.TiposDocumento.Queries.ObtenerToleranciasClienteEmpresarial;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Centros;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros;
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
/// <b>Lectores de identidad del panel y cumplimiento contextual por Centro.</b> Cabecera
/// (<see cref="ObtenerClientePorIdQuery"/>), recuentos
/// (<see cref="ObtenerResumenClienteQuery"/>) y empresas conservan los lectores del panel.
/// Subcontratas conserva las filas simples de <see cref="ObtenerSubcontratasDeClienteQuery"/>, como la maqueta.
/// Centros NO usa <c>ObtenerCentrosDeClienteQuery</c> del panel (solo trae
/// nombre y Empresa), sino <see cref="ObtenerCentrosQuery"/> con
/// <c>ClienteId</c> —la misma que la lista /centros, con el mismo alcance
/// (<c>ObtenerCentroIdsVisiblesAsync</c>)—, porque es la que trae por Centro
/// su <see cref="EstadoCentro"/>, su porcentaje y sus recuentos.
/// </para>
///
/// <para>
/// <b>Centros del Cliente empresarial, sin query nueva.</b> Se piden ordenados
/// por Estado descendente (del peor al mejor): ese orden obliga al handler a
/// tomar su «camino con estado», que ya materializa y calcula el estado de
/// TODOS los centros del cliente antes de paginar — el coste es el de todos
/// sus centros sea cual sea el tamaño de página. El tamaño de página
/// (<see cref="MaximoCentros"/>) solo acota lo que se pinta; si el cliente
/// tiene más, la lista y la franja lo avisan: los chips cuentan lo pintado y el anillo se oculta.
/// </para>
///
/// <para>
/// Qué NO enseña, igual que el panel: porcentaje de cumplimiento del Cliente
/// empresarial (no existe), Alias, Domicilio, Portal
/// principal, el «Gestor CAE de referencia» (<c>EjecutivoUsuarioId</c>) e historial de notas. La fila
/// «Gestor CAE» de la tarjeta Información sí está (<c>GestorCaeCliente</c>): enseña quién tiene Asignación de
/// Cartera sobre el Tenant y aloja «Dar acceso» y «Desasignarme». Editar la identidad
/// abre el panel en Información; editar la nota interna abre su modal de formulario.
/// El anillo cuenta Centros sin incidencias sobre Centros con gestión CAE; se omite si la lista está truncada
/// o no se han podido leer los bloqueos, para no presentar una fracción global incompleta.
/// </para>
/// </summary>
public partial class ClienteDetalle : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    /// <summary>Tope de centros pintados. El coste de la consulta no depende de él (ver la clase).</summary>
    internal const int MaximoCentros = 200;

    internal const string PestanaPorDefecto = "centros";

    /// <summary>
    /// Orden de la tira: Centros primera y por defecto porque es la única que
    /// trae estado; después Empresas, Subcontratas, Documentación, Agenda, Ficha (tolerancia
    /// de documentos por defecto) e Historial, como la maqueta del 17-10.
    /// La certificación TGSS es el primer bloque de Documentación.
    /// </summary>
    private static readonly (string Id, string Clave)[] _pestanas =
    [
        ("centros", "PestanaCentros"),
        ("empresas", "PestanaEmpresas"),
        ("subcontratas", "PestanaSubcontratas"),
        ("documentacion", "PestanaDocumentacion"),
        ("agenda", "PestanaAgenda"),
        ("ficha", "PestanaFicha"),
        ("historial", "PestanaHistorial")
    ];

    [Parameter] public Guid ClienteId { get; set; }

    /// <summary>
    /// Pestaña activa, en la URL (<c>?pestana=empresas</c>; sin parámetro,
    /// Centros), como los filtros de Centro 360: se puede enlazar y recargar
    /// sin perder dónde se estaba. Un valor desconocido cae a Centros.
    /// </summary>
    [Parameter, SupplyParameterFromQuery(Name = "pestana")] public string? Pestana { get; set; }

    /// <summary>Texto del buscador de la pestaña Centros, en la URL (<c>?q=</c>), como en Centro 360.</summary>
    [Parameter, SupplyParameterFromQuery(Name = "q")] public string? Busqueda { get; set; }

    [Parameter, SupplyParameterFromQuery(Name = "estado")] public string? Estado { get; set; }

    private string _estado = string.Empty;
    private string? _estadoDeLaUrl;
    private bool _bloqueosLeidos;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ContextWorkspaceService WorkspaceService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private ClienteDetalleDto? _detalle;
    private bool _cargando = true;
    private bool _error;
    /// <summary>
    /// La consulta devolvió null: la ficha no está en el Tenant activo (o no existe; es el mismo estado, § 4.5).
    /// No es un fallo transitorio, así que no lleva «Reintentar»: lo pinta <c>FichaNoEncontrada</c>.
    /// </summary>
    private bool _noEncontrada;

    /// <summary>Si su consulta falla se queda en null y la entradilla omite los recuentos: un fallo no se disfraza de «0 centros».</summary>
    private ResumenClienteDto? _resumen;

    private IReadOnlyList<CentroListaDto>? _centros;
    private int _totalCentros;
    private bool _drawerToleranciasVisible;

    /// <summary>
    /// Los bloqueos de acceso de los Trabajadores en los Centros de ESTE Cliente empresarial (regla única por Centro,
    /// <c>IEvaluacionDeAccesoPorCentroService</c>). «Bloqueado» es un estado del Trabajador: el Cliente empresarial dice cuántos hay y dónde.
    /// </summary>
    private IReadOnlyList<DocumentacionBloqueantePendienteDto> _bloqueos = [];
    private bool _cargandoCentros;
    private bool _errorCentros;
    private IReadOnlyList<EmpresaDeClienteDto>? _empresas;
    private bool _cargandoEmpresas;
    private bool _errorEmpresas;
    private IReadOnlyList<SubcontrataDeClienteDto>? _subcontratas;
    private bool _cargandoSubcontratas;
    private bool _errorSubcontratas;

    /// <summary>
    /// Habilita la lectura de la fila «Gestor CAE» (<c>GestorCaeCliente</c>) cuando la cadena de
    /// <see cref="CargarTodoAsync"/> terminó: sus consultas comparten el DbContext del circuito.
    /// </summary>
    private bool _gestorCaeHabilitado;

    /// <summary>
    /// Habilita la lectura de la pestaña «Documentación» (<c>DocumentosExigidosCliente</c>) cuando terminaron la cadena
    /// de <see cref="CargarTodoAsync"/> y la primera lectura de la fila «Gestor CAE»: comparten el DbContext del circuito.
    /// </summary>
    private bool _documentacionHabilitada;

    /// <summary>
    /// Turno de las lecturas que no van en la cadena de <see cref="CargarTodoAsync"/>: las de pestaña (Centros de
    /// Subcontratas, tolerancias), la fila «Gestor CAE» y la pestaña «Documentación». La cadena espera a que quede libre
    /// antes de empezar, así que una lectura cancelada sale del DbContext del circuito antes de la siguiente.
    /// </summary>
    private readonly SerieDeLecturas _serie = new();

    /// <summary>
    /// Habilita la caja «Plataforma CAE» (<c>CajaPlataformaCaeCliente</c>) cuando la fila «Gestor CAE» avisó: va
    /// detrás de ella en la misma serie, por el DbContext compartido.
    /// </summary>
    private bool _plataformaCaeHabilitada;

    /// <summary>
    /// Pendiente mientras la fila «Gestor CAE» (y su panel de acciones) y, detrás, la caja «Plataforma CAE» hacen su
    /// primera lectura: las lecturas de pestaña esperan a que terminen, por el mismo DbContext compartido. Sin valor o
    /// completada, no se espera.
    /// </summary>
    private TaskCompletionSource? _gestorCaeEnVuelo;

    /// <summary>Tope de esa espera: si la fila no avisara nunca, la pestaña no se queda colgada.</summary>
    private static readonly TimeSpan EsperaMaximaGestorCae = TimeSpan.FromSeconds(10);

    private string _pestanaActiva = PestanaPorDefecto;
    private string? _pestanaDeLaUrl;
    private string _busqueda = string.Empty;
    private string? _busquedaDeLaUrl;

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
    private bool _notaVisible;
    private bool _bajaVisible;
    private bool _dandoDeBaja;
    private string? _errorBaja;
    private int _generacion;
    private int _cargaCabecera;
    private int _cargaResumen;
    private int _cargaCentros;
    private int _cargaEmpresas;
    private int _cargaSubcontratas;

    /// <summary>
    /// Cifras de las pestañas «Documentación» y «Agenda» (maqueta Cliente 360): se leen en la cadena de la página, no al abrir
    /// la pestaña, para que el rótulo las lleve desde el principio. <c>null</c> = sin leer o con la lectura fallida: sin cifra.
    /// </summary>
    private DocumentacionExigidaClienteDto? _documentacionExigida;
    private int? _contadorDocumentacion;
    private int? _contadorAgenda;

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
        AdoptarBusquedaDeLaUrl();
        if (!string.Equals(_estadoDeLaUrl, Estado, StringComparison.Ordinal))
        {
            _estadoDeLaUrl = Estado;
            _estado = EstadoCentroUi.SeleccionValida(Estado);
        }

        if (_clienteCargado == ClienteId)
        {
            await CargarToleranciasSiProcedeAsync();
            return;
        }

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

    /// <summary>
    /// Igual que la pestaña: el texto de la URL se adopta cuando cambia ahí fuera, no en cada repintado, que pisaría
    /// lo que se está tecleando (<c>CampoTexto</c> avisa con 300 ms de retraso).
    /// </summary>
    private void AdoptarBusquedaDeLaUrl()
    {
        if (string.Equals(_busquedaDeLaUrl, Busqueda, StringComparison.Ordinal))
            return;

        _busquedaDeLaUrl = Busqueda;
        _busqueda = Busqueda ?? string.Empty;
    }

    private void BuscarCentros(string valor)
    {
        _busqueda = valor;
        _busquedaDeLaUrl = string.IsNullOrWhiteSpace(valor) ? null : valor;
        NavigationManager.ActualizarFiltroEnUrl("q", valor);
    }

    /// <summary>Quitar el filtro lo quita también de la URL: si no, recargar o compartir el enlace lo repone.</summary>
    private void QuitarFiltros()
    {
        _busqueda = _estado = string.Empty;
        _busquedaDeLaUrl = _estadoDeLaUrl = null;
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?> { ["q"] = null, ["estado"] = null });
    }

    private void CambiarEstado(string? valor)
    {
        _estado = EstadoCentroUi.SeleccionValida(valor);
        _estadoDeLaUrl = string.IsNullOrEmpty(_estado) ? null : _estado;
        NavigationManager.ActualizarFiltroEnUrl("estado", _estadoDeLaUrl);
    }

    // La maqueta solo ofrece estados presentes; conserva el seleccionado para poder reconocer el filtro sin coincidencias.
    private IReadOnlyList<OpcionFranjaEstado> OpcionesFranjaCentros => EstadoCentroUi.Franja
        .Where(o => o.Valores.Any(v => RecuentosCentros.GetValueOrDefault(v) > 0 || _estado.Split(',').Contains(v)))
        .ToList();

    private IReadOnlyDictionary<string, int> RecuentosCentros => CentrosPorBusqueda
        .GroupBy(c => c.Estado.ToString()).ToDictionary(g => g.Key, g => g.Count());

    private IReadOnlyList<CentroListaDto> CentrosConGestionCae =>
        (_centros ?? []).Where(c => c.Estado != EstadoCentro.SinGestionCae).ToList();

    private int CentrosSinIncidencias => CentrosConGestionCae.Count(c =>
        c.Estado == EstadoCentro.Vigente && c.Recuentos.TotalVencidas == 0 && c.Recuentos.TotalProximas == 0
        && !_bloqueos.Any(b => b.CentroId == c.Id));

    private bool MostrarAnillo => !_cargandoCentros && !_errorCentros && _bloqueosLeidos
        && _centros is not null && _centros.Count == _totalCentros && CentrosConGestionCae.Count > 0;

    private RenderFragment AnilloDeCentros => builder =>
    {
        builder.OpenComponent<AnilloCumplimiento>(0);
        builder.AddAttribute(1, nameof(AnilloCumplimiento.Porcentaje), (int)Math.Round(100d * CentrosSinIncidencias / CentrosConGestionCae.Count));
        builder.AddAttribute(2, nameof(AnilloCumplimiento.Detalle), Textos[CentrosConGestionCae.Count == 1 ? "FraccionCentroUno" : "FraccionCentros", CentrosSinIncidencias, CentrosConGestionCae.Count].Value);
        builder.AddAttribute(3, nameof(AnilloCumplimiento.Etiqueta), Textos[CentrosConGestionCae.Count == 1 ? "AnilloCentroUno" : "AnilloCentros", CentrosSinIncidencias, CentrosConGestionCae.Count].Value);
        builder.AddAttribute(4, nameof(AnilloCumplimiento.Tamano), TamanoAnillo.Cabecera);
        builder.CloseComponent();
    };

    private bool HayBusqueda => !string.IsNullOrWhiteSpace(_busqueda);

    /// <summary>
    /// Los Centros que casan con el buscador, por nombre y sin distinguir mayúsculas. Filtra lo ya cargado, sin consulta
    /// nueva: los indicadores de la cabecera y el contador de la pestaña siguen contando todos.
    /// </summary>
    private IReadOnlyList<CentroListaDto> CentrosPorBusqueda =>
        _centros is null ? []
        : !HayBusqueda ? _centros
        : _centros.Where(c => c.Nombre.Contains(_busqueda.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

    private IReadOnlyList<CentroListaDto> CentrosVisibles =>
        string.IsNullOrEmpty(_estado) ? CentrosPorBusqueda
        : CentrosPorBusqueda.Where(c => SeleccionEstados.Separar<EstadoCentro>(_estado).Contains(c.Estado)).ToList();

    private string Normalizar(string? pestana) =>
        pestana == "empresas" && _empresas is { Count: 1 } ? PestanaPorDefecto :
        pestana == "blindaje42" ? "documentacion" :
        _pestanas.Any(p => p.Id == pestana) ? pestana! : PestanaPorDefecto;

    private async Task CambiarPestana(string pestana)
    {
        _pestanaActiva = Normalizar(pestana);
        _pestanaDeLaUrl = _pestanaActiva == PestanaPorDefecto ? null : _pestanaActiva;
        NavigationManager.ActualizarFiltroEnUrl("pestana", _pestanaDeLaUrl);
        await EsperarGestorCaeAsync();
        await CargarToleranciasSiProcedeAsync();
    }

    /// <summary>
    /// Si la fila «Gestor CAE» sigue con su primera lectura, se espera a que avise (con tope): una pestaña que lee
    /// en paralelo chocaría con ella en el DbContext del circuito. Es la misma serie que sigue la carga inicial.
    /// </summary>
    private async Task EsperarGestorCaeAsync()
    {
        if (_gestorCaeEnVuelo is { Task.IsCompleted: false } enVuelo)
            await Task.WhenAny(enVuelo.Task, Task.Delay(EsperaMaximaGestorCae));
    }

    /// <summary>La fila «Gestor CAE» terminó: le toca a la caja «Plataforma CAE», que cierra la serie al avisar.</summary>
    private void GestorCaeCargado() => _plataformaCaeHabilitada = true;

    private void PlataformaCaeCargada()
    {
        _gestorCaeEnVuelo?.TrySetResult();
        // Terminaron la cadena (que es quien habilita la fila), la fila y la caja: ya lee la pestaña «Documentación».
        if (_gestorCaeHabilitado)
            _documentacionHabilitada = true;
    }

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

    private Task AbrirToleranciasAsync()
    {
        _drawerToleranciasVisible = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// La tolerancia cambia justo lo que la regla de acceso dice de cada Trabajador: tras guardarla se vuelven a leer los
    /// bloqueos para que la cabecera no siga contando los de antes. Informativo: si falla, se queda lo que había.
    /// </summary>
    private async Task RefrescarBloqueosAsync()
    {
        if (_detalle is not { } detalle) return;
        var generacion = _generacion;
        try
        {
            var bloqueos = await LeerBloqueosDelClienteAsync(detalle.Id);
            if (generacion != _generacion) return;
            _bloqueos = bloqueos;
            _bloqueosLeidos = true;
        }
        catch (Exception)
        {
            // Sin lectura nueva, la cabecera conserva el último recuento.
            _bloqueosLeidos = false;
        }
    }

    /// <summary>Los bloqueos visibles, quedándose con los de los Centros de este Cliente empresarial.</summary>
    private async Task<List<DocumentacionBloqueantePendienteDto>> LeerBloqueosDelClienteAsync(Guid id)
    {
        var bloqueos = await Mediator.Send(new ObtenerDocumentacionBloqueantePendienteQuery(), _cancelacion);
        return bloqueos.Where(b => b.ClienteId == id).ToList();
    }

    private void ReiniciarParaNuevoCliente()
    {
        _notaVisible = false;
        _bajaVisible = false;
        _dandoDeBaja = false;
        _errorBaja = null;
        InvalidarCargas();
        _detalle = null;
        _error = false;
        _resumen = null;
        _centros = null;
        _totalCentros = 0;
        _bloqueos = [];
        _bloqueosLeidos = false;
        _drawerToleranciasVisible = false;
        _tiposConTolerancia = null;
        _toleranciasLeidas = false;
        _cargandoTolerancias = false;
        _errorCentros = false;
        _cargandoCentros = false;
        _empresas = null;
        _errorEmpresas = false;
        _cargandoEmpresas = false;
        _subcontratas = null;
        _documentacionExigida = null;
        _contadorDocumentacion = null;
        _contadorAgenda = null;
        _gestorCaeHabilitado = false;
        _documentacionHabilitada = false;
        _plataformaCaeHabilitada = false;
        _gestorCaeEnVuelo?.TrySetResult();
        _gestorCaeEnVuelo = null;
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
        // Si una lectura de la serie sigue en vuelo (p. ej. la pestaña «Documentación» del Cliente empresarial anterior,
        // ya cancelada), la cadena espera a que salga del DbContext antes de empezar.
        try
        {
            using (await _serie.TomarAsync(_cancelacion)) { }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (generacion != _generacion) return;

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
        if (generacion != _generacion) return;
        await CargarContadoresAsync(generacion);
        if (generacion != _generacion) return;
        await CargarToleranciasSiProcedeAsync();
        if (generacion != _generacion) return;
        // Solo la primera vez por Cliente empresarial: GestorCaeCliente avisa cuando Cargar pasa de false a true, así que
        // en una segunda cadena (Reintentar) con la fila ya habilitada no llegaría nadie a completar un aviso nuevo.
        if (!_gestorCaeHabilitado)
        {
            _gestorCaeEnVuelo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _gestorCaeHabilitado = true;
        }
        StateHasChanged();
        // La pestaña «Documentación» se habilita cuando la fila «Gestor CAE» avisa de su primera lectura
        // (GestorCaeCargado), no esperándola aquí: la cadena termina como antes, sin un render tardío de la página.
    }

    /// <summary>Un doble clic en «Reintentar» no lanza una segunda cadena: la primera ya puso _cargando.</summary>
    private Task ReintentarAsync() => _cargando ? Task.CompletedTask : CargarTodoAsync();

    private async Task CargarCabeceraAsync()
    {
        var carga = ++_cargaCabecera;
        var id = ClienteId;
        _cargando = true;
        _error = false;
        _noEncontrada = false;

        try
        {
            var detalle = await Mediator.Send(new ObtenerClientePorIdQuery(id), _cancelacion);
            if (carga != _cargaCabecera) return;
            _detalle = detalle;
            _noEncontrada = detalle is null;
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

            // Informativo, como el resto de indicadores: si falla, la cabecera no los pinta (no tumba la pestaña).
            try
            {
                var bloqueos = await LeerBloqueosDelClienteAsync(id);
                if (carga != _cargaCentros) return;
                _bloqueos = bloqueos;
                _bloqueosLeidos = true;
            }
            catch (Exception)
            {
                if (carga == _cargaCentros)
                {
                    _bloqueos = [];
                    _bloqueosLeidos = false;
                }
            }
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
            _pestanaActiva = Normalizar(_pestanaDeLaUrl);
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

    private async Task ReintentarSubcontratasAsync()
    {
        if (_cargandoSubcontratas) return;
        await CargarSubcontratasAsync();
    }

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
    /// Cifras de «Documentación» (los requisitos que exige el Cliente empresarial, más la fila del certificado de la Seguridad
    /// Social si tiene Empresas) y de «Agenda» (sus contactos). Informativas: si una lectura falla la pestaña se queda sin cifra.
    /// Los requisitos se pasan a la lista, que no los repite al abrir la pestaña.
    /// </summary>
    private async Task CargarContadoresAsync(int generacion)
    {
        if (_detalle is not { } detalle) return;
        DocumentacionExigidaClienteDto? exigida = null;
        int? documentacion = null;
        int? agenda = null;
        try
        {
            exigida = await Mediator.Send(new ObtenerDocumentacionExigidaDeClienteQuery(detalle.Id), _cancelacion);
            if (exigida is not null)
                documentacion = exigida.Requisitos.Count + (_empresas is { Count: > 0 } ? 1 : 0);
        }
        catch (Exception)
        {
            exigida = null;
            documentacion = null;
        }

        try
        {
            agenda = (await Mediator.Send(new ObtenerAgendaContactosQuery(TipoPropietarioAgenda.Cliente, detalle.Id), _cancelacion)).Count;
        }
        catch (Exception)
        {
            agenda = null;
        }

        if (generacion != _generacion) return;
        _documentacionExigida = exigida;
        _contadorDocumentacion = documentacion;
        _contadorAgenda = agenda;
        StateHasChanged();
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

    /// <summary>Empresas que participan en el Centro del Cliente empresarial, según la lectura disponible.</summary>
    private string? EmpresasDelCentro(CentroListaDto centro) =>
        !string.IsNullOrWhiteSpace(centro.EmpresaRazonSocial)
            ? Textos["EmpresasDelCentro", centro.EmpresaRazonSocial].Value
            : null;

    /// <summary>Motivos documentales, bloqueos de plataforma y Trabajadores bloqueados, sin partes en cero ni confundir pendientes con vencidos.</summary>
    private string MotivosCentro(CentroListaDto centro)
    {
        var partes = new List<string>();
        if (centro.Recuentos.VencidasPorFecha.Count > 0)
            partes.Add(Plural(centro.Recuentos.VencidasPorFecha.Count, "DetalleVencidosUno", "DetalleVencidosVarios"));
        if (centro.Recuentos.Pendientes.Count > 0)
            partes.Add(Textos[centro.Recuentos.Pendientes.Count == 1 ? "DetallePendienteUno" : "DetallePendientes", centro.Recuentos.Pendientes.Count]);
        var bloqueados = _bloqueos.Where(b => b.CentroId == centro.Id).Select(b => b.TrabajadorId).Distinct().Count();
        if (bloqueados > 0)
            partes.Add(Textos[bloqueados == 1 ? "DetalleTrabajadorBloqueadoUno" : "DetalleTrabajadoresBloqueados", bloqueados]);
        if (centro.Recuentos.TotalProximas > 0)
            partes.Add(Plural(centro.Recuentos.TotalProximas, "DetalleProximosUno", "DetalleProximosVarios"));
        return string.Join(" · ", partes);
    }

    /// <summary>
    /// Maqueta vigente: Empresas y recuentos en líneas distintas; el bloqueo de la plataforma CAE se rotula junto al nombre.
    /// </summary>
    private IReadOnlyList<string> LineasDetalleCentro(CentroListaDto centro)
    {
        var lineas = new List<string>();
        if (centro.Estado == EstadoCentro.SinGestionCae)
            lineas.Add(Textos["CentroSinGestionCaeLinea"].Value);
        if (EmpresasDelCentro(centro) is { } contratista)
            lineas.Add(contratista);
        var motivos = MotivosCentro(centro);
        if (motivos.Length > 0)
            lineas.Add(motivos);
        return lineas;
    }

    /// <summary>Sin líneas no se pasa la ranura: FilaRelacion pintaría una segunda línea vacía.</summary>
    private RenderFragment? FragmentoDetalleCentro(CentroListaDto centro)
    {
        var lineas = LineasDetalleCentro(centro);
        if (lineas.Count == 0) return null;
        return builder =>
        {
            foreach (var linea in lineas)
            {
                builder.OpenElement(0, "span");
                builder.AddAttribute(1, "class", "cliente360-linea-detalle");
                builder.AddContent(2, linea);
                builder.CloseElement();
            }
        };
    }

    /// <summary>Maqueta 17-10 (.fila-tinte-rojo / .fila-tinte-ambar): la fila se tiñe con el tono de su estado cuando hay problema.</summary>
    private static TonoFila? TonoDeFilaCentro(CentroListaDto centro) =>
        EstadoCentroUi.Tono(centro.Estado, centro.CumplimientoPorcentaje) switch
        {
            TonoBadge.Peligro => TonoFila.Peligro,
            TonoBadge.Advertencia => TonoFila.Advertencia,
            _ => null
        };

    /// <summary>
    /// El peor estado de los Centros con gestión CAE, para la pastilla de la cabecera (maqueta 17-10). Con la lista truncada
    /// en <see cref="MaximoCentros"/> no se afirma nada global, igual que el anillo.
    /// </summary>
    private CentroListaDto? PeorCentroConGestionCae =>
        _centros is not null && !_errorCentros && !_cargandoCentros && _centros.Count == _totalCentros
            ? CentrosConGestionCae.MaxBy(c => CalculadoraEstadoCentro.Gravedad(c.Estado))
            : null;

    // ── Tolerancia por defecto (pestaña Ficha) ───────────────────────────

    private int? _tiposConTolerancia;
    private bool _toleranciasLeidas;
    private bool _cargandoTolerancias;

    private string? ResumenTolerancias => _tiposConTolerancia switch
    {
        null => null,
        0 => Textos["ToleranciaResumenNinguno"].Value,
        var n => Plural(n.Value, "ToleranciaResumenUno", "ToleranciaResumenVarios")
    };

    private Task CargarToleranciasSiProcedeAsync() =>
        _pestanaActiva == "ficha" && !_toleranciasLeidas ? CargarToleranciasAsync() : Task.CompletedTask;

    /// <summary>Informativo: si la lectura falla la tarjeta se queda con su descripción, sin resumen.</summary>
    private async Task CargarToleranciasAsync()
    {
        if (_cargandoTolerancias || _detalle is not { } detalle) return;
        _cargandoTolerancias = true;
        var generacion = _generacion;
        try
        {
            using var turno = await _serie.TomarAsync(_cancelacion);
            var tolerancias = await Mediator.Send(new ObtenerToleranciasClienteEmpresarialQuery(detalle.Id), _cancelacion);
            if (generacion != _generacion) return;
            _tiposConTolerancia = tolerancias.Count(t => t.ToleranciaDias > 0);
            _toleranciasLeidas = true;
        }
        catch (Exception)
        {
            _tiposConTolerancia = null;
        }
        finally
        {
            _cargandoTolerancias = false;
        }
    }

    /// <summary>Tras guardar el cajón: bloqueos de la cabecera y resumen de la ficha al día.</summary>
    private async Task TrasGuardarToleranciasAsync()
    {
        await RefrescarBloqueosAsync();
        _toleranciasLeidas = false;
        await CargarToleranciasSiProcedeAsync();
    }

    private IReadOnlyList<PestanaDefinicion> PestanasConRecuento =>
        _pestanas.Where(p => p.Id != "empresas" || _empresas is not { Count: 1 }).Select(p =>
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
                "documentacion" when _contadorDocumentacion is > 0 and var documentos =>
                    definicion with { Contador = new ContadorPestana(documentos, Plural(documentos, "GlosaDocumentoUno", "GlosaDocumentoVarios")) },
                "agenda" when _contadorAgenda is > 0 and var contactos =>
                    definicion with { Contador = new ContadorPestana(contactos, Plural(contactos, "GlosaContactoUno", "GlosaContactoVarios")) },
                _ => definicion
            };
        }).ToList();

    // ── Navegación ────────────────────────────────────────────────────────

    private void IrABreadcrumb(int indice)
    {
        if (indice == 0)
            NavigationManager.NavigateTo("/clientes");
    }

    /// <summary>
    /// Quien mira acaba de desasignarse, desde la fila «Gestor CAE», de su apoyo sobre el Tenant activo: todo lo que
    /// esta página tiene pintado es de un Tenant al que ya no accede. Va a «Mi trabajo» con <c>sinAcceso=true</c>
    /// para avisarlo; <c>forceLoad</c> hace que el arranque normal vuelva a resolver el Tenant activo y el acceso.
    /// </summary>
    private void IrAMiTrabajoTrasDesasignarme() => NavigationManager.NavigateTo("/mi-trabajo?sinAcceso=true", forceLoad: true);

    private Task AbrirInformacionAsync() =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Cliente, ClienteId, _detalle?.RazonSocial ?? string.Empty, "informacion");

    private void AbrirBaja()
    {
        _errorBaja = null;
        _bajaVisible = true;
    }

    private async Task DarDeBajaAsync()
    {
        if (_dandoDeBaja || !_bajaVisible || _detalle is not { } detalle) return;
        var generacion = _generacion;
        _dandoDeBaja = true;
        _errorBaja = null;
        try
        {
            var resultado = await Mediator.Send(new EliminarClienteCommand(detalle.Id));
            if (generacion != _generacion) return;
            if (resultado.EsFallido)
                _errorBaja = resultado.Error.Mensaje;
            else
                NavigationManager.NavigateTo("/clientes");
        }
        catch (Exception)
        {
            if (generacion == _generacion)
                _errorBaja = Textos["BajaError"];
        }
        finally
        {
            if (generacion == _generacion)
                _dandoDeBaja = false;
        }
    }

    private Task AbrirCentroAsync(CentroListaDto centro) =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Centro, centro.Id, centro.Nombre, "informacion");

    private Task AbrirEmpresaAsync(EmpresaDeClienteDto empresa) =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Empresa, empresa.Id, empresa.RazonSocial, "informacion");

    private Task AbrirSubcontrataAsync(SubcontrataDeClienteDto subcontrata) =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Subcontrata, subcontrata.Id, subcontrata.RazonSocial, "informacion");
}
