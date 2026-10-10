using CaeManager.Web.Components;
using CaeManager.Application.Gestiones.Commands.CompletarGestion;
using CaeManager.Application.Gestiones.Commands.EliminarGestion;
using CaeManager.Application.Gestiones.Commands.RestaurarGestion;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Common;
using CaeManager.Domain.Gestiones;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Components.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.QuickGrid;

namespace CaeManager.Web.Features.Gestiones.Pages;

public partial class Gestiones : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    private readonly PaginationState _paginacion = new() { ItemsPerPage = 20 };

    // H2 (Project-Hydra-Negocio/tecnico/docs/ux-audit/02-clientes.md): paginador único en español, ver Clientes.razor.cs.
    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_totalElementos / (double)_paginacion.ItemsPerPage));

    private Task CambiarPaginaAsync(int pagina) => _paginacion.SetCurrentPageIndexAsync(pagina - 1);

    // H5 (Project-Hydra-Negocio/tecnico/docs/ux-audit/05-trabajadores-vehiculos.md): selector de tamaño de página, compartido por PaginadorSimple.razor.
    // Una sola petición, por el mismo motivo que RecargarAsync.
    private Task CambiarTamanoPaginaAsync(int tamano)
    {
        _paginacion.ItemsPerPage = tamano;
        return _paginacion.SetCurrentPageIndexAsync(0);
    }

    private QuickGrid<GestionListaDto>? _grid;
    private TemplateColumn<GestionListaDto>? _columnaTrabajador;
    private ColumnBase<GestionListaDto>? _ultimaColumnaOrden;
    private bool _ultimoOrdenAscendente = true;
    private bool _ordenTrabajadorPorCentro;
    private bool _ordenTrabajadorPendiente;
    private SortDirection _direccionOrdenPendiente;
    private bool _desechado;
    private Guid? _idEnfocado;
    private List<GestionListaDto> _elementosPagina = [];
    private static readonly GridSort<GestionListaDto> OrdenTrabajadorListado = GridSort<GestionListaDto>.ByAscending(g => g.TrabajadorNombre);
    private static readonly GridSort<GestionListaDto> OrdenCentroListado = GridSort<GestionListaDto>.ByAscending(g => g.CentroNombre);
    private static readonly GridSort<GestionListaDto> OrdenCreadaListado = GridSort<GestionListaDto>.ByAscending(g => g.CreadoEnUtc);

    public void Dispose() => _desechado = true;

    private void CambiarCampoOrdenTrabajador(bool porCentro)
    {
        _ordenTrabajadorPorCentro = porCentro;
        // Misma columna conserva sentido; otra columna empieza ascendente.
        _direccionOrdenPendiente = ReferenceEquals(_ultimaColumnaOrden, _columnaTrabajador) && !_ultimoOrdenAscendente
            ? SortDirection.Descending : SortDirection.Ascending;
        _ordenTrabajadorPendiente = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var grid = _grid;
        var columna = _columnaTrabajador;
        if (_desechado || !_ordenTrabajadorPendiente || grid is null || columna is null)
            return;

        // QuickGrid debe recibir primero el SortBy nuevo; consumir antes del await evita repetir.
        _ordenTrabajadorPendiente = false;
        await grid.SortByColumnAsync(columna, _direccionOrdenPendiente);
        if (!_desechado && ReferenceEquals(grid, _grid))
            await grid.HideColumnOptionsAsync();
    }

    private string _busqueda = string.Empty;
    private string _filtroEstado = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    /// <summary>
    /// Número de la última carga pedida. Cada carga captura el suyo al empezar
    /// y, al volver del <c>await</c>, solo escribe estado si sigue siendo la
    /// vigente: si mientras tanto cambió el filtro, la búsqueda, la página o el
    /// orden, su respuesta es de otra pregunta. QuickGrid ya descarta las
    /// FILAS de una carga superada, pero no sabe nada de
    /// <see cref="_totalElementos"/> ni de <see cref="_errorCarga"/>, que son
    /// de esta página: sin esto, una respuesta lenta del filtro anterior
    /// pisaba el total del nuevo y el estado vacío desaparecía dejando una
    /// rejilla sin filas.
    /// </summary>
    private int _cargaVigente;

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private bool _eliminando;

    /// <summary>
    /// Fila abierta en la vista rápida. Es la propia fila de la lista: no hay
    /// consulta por Id de Gestion, y así el panel no puede discrepar de ella.
    /// </summary>
    private GestionListaDto? _vistaRapida;

    /// <summary>
    /// Gestiones cuyo cambio de estado está viajando. Guarda de reentrada POR
    /// GESTIÓN: pulsar otra vez sobre la misma (el botón de la vista rápida y
    /// el del menú de su fila son dos disparadores de lo mismo) no manda un
    /// segundo comando; pulsar sobre OTRA sí sigue funcionando — una bandera
    /// única lo habría descartado sin decir nada.
    /// </summary>
    private readonly HashSet<Guid> _cambiandoEstado = [];

    private GridItemsProvider<GestionListaDto>? _proveedorElementos;

    /// <summary>
    /// Destino del botón «Ir a Mi trabajo» del estado vacío: el mismo que el de la entrada del menú
    /// (<see cref="CatalogoMenuLateral.RutaMiTrabajo"/>). Null hasta que la lista sale vacía por
    /// primera vez; el número de Tenants autorizados no cambia durante la vida de la página.
    /// </summary>
    private string? _rutaMiTrabajo;

    private IReadOnlyList<OpcionFranjaEstado> OpcionesEstado =>
    [
        new(Textos["FiltroPendientes"], TonoEstado(EstadoGestion.Pendiente), nameof(EstadoGestion.Pendiente)),
        new(Textos["FiltroCompletadas"], TonoEstado(EstadoGestion.Completada), nameof(EstadoGestion.Completada))
    ];

    /// <summary>Gestiones por estado para la franja, sin el filtro de estado aplicado. <c>null</c> hasta la primera carga.</summary>
    private IReadOnlyDictionary<string, int>? _recuentosPorEstado;

    /// <summary>
    /// La selección de estados que llega de la URL reducida a nombres de <see cref="EstadoGestion"/>; lo demás se
    /// descarta. Cadena vacía si no queda ninguno.
    /// </summary>
    private static string EstadosValidos(string? seleccion) =>
        SeleccionEstados.Unir(SeleccionEstados.Separar<EstadoGestion>(seleccion).Select(e => e.ToString())) ?? string.Empty;

    /// <summary>
    /// El estado por el que filtra la consulta, que admite uno solo: con los dos marcados no hay nada que
    /// filtrar (son todos los estados que existen).
    /// </summary>
    private EstadoGestion? EstadoDeLaConsulta =>
        SeleccionEstados.Separar<EstadoGestion>(_filtroEstado) is [var unico] ? unico : null;

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "estado")]
    public string? EstadoInicial { get; set; }

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    /// <summary>Orden de columna (<c>?orden=creada-desc</c>). Sin él, el de fábrica: por estado.</summary>
    [SupplyParameterFromQuery(Name = "orden")]
    public string? OrdenInicial { get; set; }

    /// <summary>
    /// El orden de columna viaja en la URL y forma parte de la vista. La rejilla nace ordenada por
    /// estado; «trabajador» y «centro» son la misma columna.
    /// </summary>
    private readonly OrdenDeRejilla _orden = new(
    [
        ("trabajador", nameof(GestionListaDto.TrabajadorNombre)),
        ("centro", nameof(GestionListaDto.CentroNombre)),
        ("tipo", nameof(GestionListaDto.TipoDocumentoNombre)),
        ("estado", nameof(GestionListaDto.Estado)),
        ("creada", nameof(GestionListaDto.CreadoEnUtc)),
    ], claveDeFabrica: "estado");

    /// <summary>El orden que llega (URL, filtro guardado o vista recordada). Si cambia, la rejilla se remonta ya ordenada.</summary>
    private bool LeerOrden(string? valor)
    {
        if (!_orden.Leer(valor))
            return false;

        if (_orden.Propiedad is nameof(GestionListaDto.TrabajadorNombre) or nameof(GestionListaDto.CentroNombre))
            _ordenTrabajadorPorCentro = _orden.Propiedad == nameof(GestionListaDto.CentroNombre);
        return true;
    }

    protected override void OnInitialized() => _proveedorElementos = ProveerElementosAsync;

    /// <summary>La URL es la fuente de verdad del filtro (P1-18) — ver el resto de listados.</summary>
    protected override void OnParametersSet()
    {
        var deLaUrl = EstadosValidos(EstadoInicial);
        if (deLaUrl != _filtroEstado)
            _filtroEstado = deLaUrl;

        var busquedaDeLaUrl = TerminoBusquedaInicial ?? string.Empty;
        if (busquedaDeLaUrl != _busqueda)
            _busqueda = busquedaDeLaUrl;

        LeerOrden(OrdenInicial);
    }

    private async ValueTask<GridItemsProviderResult<GestionListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<GestionListaDto> request)
    {
        if (_desechado)
            return GridItemsProviderResult.From(new List<GestionListaDto>(), 0);

        // Todo lo que define la pregunta se lee ANTES del await.
        var carga = ++_cargaVigente;
        var (ordenarPor, descendente) = LecturaOrden.Leer(request);
        _ultimaColumnaOrden = request.SortByColumn;
        _ultimoOrdenAscendente = request.SortByAscending;
        if (_orden.Anotar(ordenarPor, descendente))
            NavigationManager.ActualizarFiltroEnUrl("orden", _orden.EnUrl);
        (_ordenExportar, _descendenteExportar) = (ordenarPor, descendente);
        _elementosPagina = [];
        _idEnfocado = null;
        var consulta = new ObtenerGestionesQuery(
            Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
            Estado: EstadoDeLaConsulta,
            TrabajadorId: null,
            Pagina: (request.StartIndex / _paginacion.ItemsPerPage) + 1,
            TamanoPagina: _paginacion.ItemsPerPage,
            OrdenarPor: ordenarPor,
            Descendente: descendente,
            ConRecuentosPorEstado: true);

        _cargando = true;
        _errorCarga = false;

        try
        {
            var resultado = await Mediator.Send(consulta, request.CancellationToken);

            if (_desechado || carga != _cargaVigente)
                return GridItemsProviderResult.From(new List<GestionListaDto>(), 0);

            // El estado vacío sin filtros enlaza a Mi trabajo, y su destino depende de cuántos
            // Tenants tiene autorizados quien mira. Se pregunta solo cuando ese estado va a pintarse,
            // una vez, y DESPUÉS de la consulta principal, nunca a la vez: el DbContext del circuito
            // no admite dos a un tiempo.
            if (resultado.TotalElementos == 0 && !HayFiltrosActivos && _rutaMiTrabajo is null)
            {
                var ruta = await ResolverRutaMiTrabajoAsync(request.CancellationToken);
                if (_desechado || carga != _cargaVigente)
                    return GridItemsProviderResult.From(new List<GestionListaDto>(), 0);

                _rutaMiTrabajo = ruta;
            }

            _totalElementos = resultado.TotalElementos;
            _recuentosPorEstado = resultado.RecuentosPorEstado;
            _elementosPagina = resultado.Elementos.ToList();

            return GridItemsProviderResult.From(resultado.Elementos.ToList(), resultado.TotalElementos);
        }
        catch (Exception) when (_desechado || carga != _cargaVigente)
        {
            // Una carga superada que falla (o que QuickGrid canceló) no es un
            // error de la vigente: no puede tapar su resultado.
            return GridItemsProviderResult.From(new List<GestionListaDto>(), 0);
        }
        catch (Exception)
        {
            _errorCarga = true;
            return GridItemsProviderResult.From(new List<GestionListaDto>(), 0);
        }
        finally
        {
            if (!_desechado && carga == _cargaVigente)
            {
                _cargando = false;
                StateHasChanged();
            }
        }
    }

    /// <summary>
    /// La pregunta es accesoria: solo decide adónde lleva un botón. Si falla, la lista vacía sigue
    /// siendo una lista vacía y no un error de carga; el destino queda sin resolver (el botón cae en
    /// <c>/bandeja</c>, que existe para cualquiera con Mi trabajo) y se vuelve a preguntar en la
    /// siguiente carga vacía.
    /// </summary>
    private async Task<string?> ResolverRutaMiTrabajoAsync(CancellationToken cancellationToken)
    {
        try
        {
            var autorizados = await Mediator.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);
            return CatalogoMenuLateral.RutaMiTrabajo(autorizados.Count > 1);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        NavigationManager.ActualizarFiltroEnUrl("q", valor);
        await RecargarAsync();
    }

    private async Task FiltrarPorEstadoAsync(string? valor)
    {
        _filtroEstado = valor ?? string.Empty;
        NavigationManager.ActualizarFiltroEnUrl("estado", valor);
        await RecargarAsync();
    }

    /// <summary>
    /// Los dos filtros de la barra. Separa "sin gestiones" de "ninguna con
    /// estos filtros": el texto sin filtrar explica cómo se generan las
    /// gestiones, y con un estado puesto se lee como que el mecanismo falla.
    /// </summary>
    private bool HayFiltrosActivos =>
        !string.IsNullOrWhiteSpace(_busqueda) || !string.IsNullOrWhiteSpace(_filtroEstado);

    /// <summary>
    /// Cuántas coinciden. Sin filtros no habla de ninguno; con filtros dice que
    /// el número es el de las que coinciden, no el de la cartera.
    /// </summary>
    private string TextoConteo
    {
        get
        {
            var clave = (HayFiltrosActivos, _totalElementos == 1) switch
            {
                (true, true) => "ConteoFiltradoUno",
                (true, false) => "ConteoFiltradoVarios",
                (false, true) => "ConteoUno",
                (false, false) => "ConteoVarios",
            };
            return Textos[clave, _totalElementos];
        }
    }

    /// <summary>
    /// Quita los dos filtros en una sola recarga y en una sola navegación. Los
    /// dos viven además en la URL y se limpian allí: <see cref="OnParametersSet"/>
    /// re-sincroniza desde ella en cada navegación dentro de la página, y un
    /// filtro que siguiera en la URL volvería a aplicarse.
    /// </summary>
    private async Task LimpiarFiltrosAsync()
    {
        _busqueda = string.Empty;
        _filtroEstado = string.Empty;
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?> { ["q"] = null, ["estado"] = null });
        await RecargarAsync();
    }

    // ---- Filtros guardados (pieza compartida FiltrosGuardadosDeListado) ----

    private const string PantallaDeFiltrosGuardados =
        CaeManager.Application.Configuracion.Commands.GuardarFiltro.PantallasConFiltrosGuardados.Gestiones;

    /// <summary>
    /// Lista blanca de los parámetros de VISTA de la URL: lo que guarda y aplica un filtro guardado, y lo que
    /// recuerda la vista recordada (<see cref="VistaRecordadaDeListado"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> ParametrosDeVista = ["q", "estado", "orden"];

    private readonly ConexionFiltrosGuardados _filtrosGuardados = new();
    private readonly ConexionVistaRecordada _vistaRecordada = new();

    /// <summary>
    /// Un filtro guardado define la vista entera: lo que no trae se quita. El estado pasa por la misma
    /// validación que el de la URL (<see cref="EstadosValidos"/>). La URL se escribe en una sola navegación
    /// y se recarga aquí: <see cref="OnParametersSet"/> sincroniza los campos, pero no recarga.
    /// </summary>
    private async Task AplicarVistaGuardadaAsync(IReadOnlyDictionary<string, string?> vista)
    {
        _busqueda = vista.GetValueOrDefault("q") ?? string.Empty;
        _filtroEstado = EstadosValidos(vista.GetValueOrDefault("estado"));
        var cambiaElOrden = LeerOrden(vista.GetValueOrDefault("orden"));
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["q"] = _busqueda,
            ["estado"] = _filtroEstado,
            ["orden"] = _orden.EnUrl,
        });

        // Con otro orden la rejilla se remonta en el siguiente render y pide ella los datos:
        // refrescar además la saliente sería pedirlos dos veces.
        if (cambiaElOrden && _paginacion.CurrentPageIndex == 0)
            StateHasChanged();
        else
            await RecargarAsync();
    }

    /// <summary>Mismo motivo que <see cref="LimpiarFiltrosAsync"/>: la búsqueda se quita también de la URL.</summary>
    private async Task QuitarBusquedaAsync()
    {
        _busqueda = string.Empty;
        NavigationManager.ActualizarFiltroEnUrl("q", string.Empty);
        await RecargarAsync();
    }

    /// <summary>Mismo motivo que <see cref="LimpiarFiltrosAsync"/>: el estado se quita también de la URL.</summary>
    private async Task QuitarFiltroEstadoAsync()
    {
        _filtroEstado = string.Empty;
        NavigationManager.ActualizarFiltroEnUrl("estado", string.Empty);
        await RecargarAsync();
    }

    /// <summary>
    /// Vuelve a la página 1 y pide la lista UNA vez.
    /// <see cref="PaginationState.SetCurrentPageIndexAsync"/> no tiene guarda de
    /// igualdad: asigna el índice e invoca <c>CurrentPageItemsChanged</c> siempre,
    /// cambie o no la página, y QuickGrid tiene ahí suscrito su
    /// <c>RefreshDataCoreAsync</c>. Así que avisar a la paginación YA es pedir los
    /// datos; añadir <c>RefreshDataAsync</c> lanzaba dos consultas idénticas por
    /// cada búsqueda o filtro aunque el total no cambiara (medido en
    /// <c>GestionesListaGen2Tests</c>).
    ///
    /// <para>
    /// Lo que esto no evita: si la respuesta trae un total distinto del anterior,
    /// QuickGrid puede volver a pedir la misma página por su cuenta. Su guarda
    /// compara un hash de <c>PaginationState</c> que incluye <c>TotalItemCount</c>
    /// y que se guarda ANTES de conocer el total nuevo, así que queda rancio y la
    /// siguiente pasada de parámetros vuelve a consultar. Es de QuickGrid, no de
    /// esta página.
    /// </para>
    /// </summary>
    private async Task RecargarAsync()
    {
        if (_grid is not null && _paginacion.CurrentPageIndex == 0)
            await _grid.RefreshDataAsync();
        else
            await _paginacion.SetCurrentPageIndexAsync(0);

        StateHasChanged();
    }

    private static TonoBadge TonoEstado(EstadoGestion estado) =>
        estado == EstadoGestion.Completada ? TonoBadge.Exito : TonoBadge.Advertencia;

    private string TextoEstado(EstadoGestion estado) =>
        estado == EstadoGestion.Completada ? Textos["EstadoCompletada"] : Textos["EstadoPendiente"];

    /// <summary>
    /// Motivo bajo la pastilla de estado: cuánto lleva abierta una Gestión pendiente. La completada no
    /// lleva motivo (no pide acción).
    /// </summary>
    private string? MotivoEstado(GestionListaDto gestion)
    {
        if (gestion.Estado != EstadoGestion.Pendiente)
            return null;

        return DiasAbierta(gestion.CreadoEnUtc, DiaDeNegocio.Hoy()) switch
        {
            0 => Textos["MotivoAbiertaHoy"],
            1 => Textos["MotivoAbiertaHaceUnDia"],
            var dias => Textos["MotivoAbiertaHaceDias", dias]
        };
    }

    /// <summary>
    /// Días naturales que lleva abierta una Gestión, contados entre días de negocio (Europe/Madrid): el
    /// instante de creación se lleva a su día de negocio antes de restar, así que una Gestión creada a las
    /// 23:30 UTC —ya el día siguiente en Madrid— no cuenta un día de más. Nunca negativo: una creación
    /// posterior a «hoy» (relojes desacompasados) se lee como abierta hoy.
    /// </summary>
    private static int DiasAbierta(DateTime creadoEnUtc, DateOnly hoy) =>
        Math.Max(0, hoy.DayNumber - DiaDeNegocio.De(creadoEnUtc).DayNumber);

    private async Task ManejarAtajoAsync(string tecla)
    {
        if (_desechado || _elementosPagina.Count == 0) return;

        switch (tecla)
        {
            case "j":
                var siguiente = _idEnfocado is null ? -1 : _elementosPagina.FindIndex(g => g.Id == _idEnfocado);
                _idEnfocado = _elementosPagina[Math.Min(siguiente + 1, _elementosPagina.Count - 1)].Id;
                break;
            case "k":
                var anterior = _idEnfocado is null ? 0 : _elementosPagina.FindIndex(g => g.Id == _idEnfocado);
                _idEnfocado = _elementosPagina[Math.Max(anterior - 1, 0)].Id;
                break;
            case "Enter":
                if (_elementosPagina.FirstOrDefault(g => g.Id == _idEnfocado) is { } fila)
                    AbrirVistaRapida(fila);
                break;
                // Sin selección múltiple: x no tiene ninguna acción en Gestiones.
        }

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// «fila-pulsable»: un clic en cualquier punto de la fila abre la vista rápida. QuickGrid no
    /// expone el clic de fila, así que lo atiende el módulo de atajos de lista, que pulsa el
    /// «nombre-abre-vista-rapida» de la fila.
    /// </summary>
    private string ObtenerClaseFila(GestionListaDto fila) =>
        fila.Id == (_idEnfocado ?? _vistaRapida?.Id) ? "fila-pulsable fila-enfocada" : "fila-pulsable";

    private void AbrirVistaRapida(GestionListaDto fila)
    {
        _idEnfocado = fila.Id;
        _vistaRapida = fila;
    }

    private void CerrarVistaRapida() => _vistaRapida = null;

    /// <summary>
    /// Los 360 se abren desde la vista rápida: se cierra primero para no dejar
    /// dos paneles laterales apilados sobre la lista.
    /// </summary>
    private Task AbrirWorkspaceAsync(EntidadWorkspace tipo, Guid id, string titulo, string pestana)
    {
        _vistaRapida = null;
        return WorkspaceService.AbrirAsync(tipo, id, titulo, pestana);
    }

    private async Task CambiarEstadoAsync(Guid id, bool completada)
    {
        if (!_cambiandoEstado.Add(id))
            return;

        try
        {
            var resultado = await Mediator.Send(new CompletarGestionCommand(id, completada));
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            // Solo si la vista rápida sigue enseñando ESA gestión: mientras el
            // comando viajaba se pudo abrir otra fila, y a esa no le ha pasado nada.
            if (_vistaRapida is { } vista && vista.Id == id)
                _vistaRapida = vista with { Estado = completada ? EstadoGestion.Completada : EstadoGestion.Pendiente };

            await RecargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["ToastErrorEstado"], TonoToast.Error);
        }
        finally
        {
            _cambiandoEstado.Remove(id);
        }
    }

    private void AbrirEliminar(Guid id)
    {
        _idAEliminar = id;
        _confirmarEliminarVisible = true;
    }

    /// <summary>«Deshacer» del aviso tras eliminar — ver RestaurarGestionCommand.</summary>
    private async Task DeshacerEliminarAsync(Guid id)
    {
        // Guarda por elemento: dos pulsaciones en «Deshacer» del mismo aviso no mandan dos restauraciones.
        if (!_restaurando.Add(id)) return;

        try
        {
            var resultado = await Mediator.Send(new RestaurarGestionCommand(id));

            ToastService.Mostrar(
                resultado.EsExitoso ? Textos["ToastRestaurada"].Value : resultado.Error.Mensaje,
                resultado.EsExitoso ? TonoToast.Exito : TonoToast.Error);

            if (resultado.EsExitoso)
                await RecargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["ErrorRestaurar"], TonoToast.Error);
        }
        finally
        {
            _restaurando.Remove(id);
        }
    }

    private readonly HashSet<Guid> _restaurando = [];

    private async Task ConfirmarEliminarAsync()
    {
        _eliminando = true;
        var id = _idAEliminar;

        try
        {
            var resultado = await Mediator.Send(new EliminarGestionCommand(id));

            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
            }
            else
            {
                ToastService.Mostrar(Textos["ToastEliminada"], TonoToast.Exito, Textos["ToastAccionDeshacer"], () => DeshacerEliminarAsync(id));
                _confirmarEliminarVisible = false;

                // Una vista rápida abierta sobre la gestión borrada enseñaría,
                // con sus botones activos, algo que ya no está en la lista.
                if (_vistaRapida?.Id == id)
                    _vistaRapida = null;

                await RecargarAsync();
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar(Textos["ToastErrorEliminar"], TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }

    // ---- Exportar esta vista ----

    private string? _ordenExportar;
    private bool _descendenteExportar;

    /// <summary>
    /// Los criterios de la vista con los nombres de parámetro de <c>/gestiones/exportar.xlsx</c>:
    /// los mismos que <see cref="ProveerElementosAsync"/> pasa a la consulta del listado.
    /// </summary>
    private Dictionary<string, string?> CriteriosExportar => new()
    {
        ["q"] = _busqueda,
        ["estado"] = _filtroEstado,
        ["orden"] = _ordenExportar,
        ["desc"] = _descendenteExportar ? "true" : null,
    };
}
