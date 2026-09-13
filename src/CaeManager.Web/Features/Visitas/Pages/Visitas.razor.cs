using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Comunicaciones.Queries.ObtenerSugerenciaVisitaCorreo;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Visitas.Commands.CrearVisita;
using CaeManager.Application.Visitas.Commands.EditarVisita;
using CaeManager.Application.Visitas.Commands.EliminarVisita;
using CaeManager.Application.Visitas.Commands.EliminarVisitas;
using CaeManager.Application.Visitas.Commands.MarcarNotificadoCliente;
using CaeManager.Application.Visitas.Queries.ObtenerDetalleVisita;
using CaeManager.Application.Visitas.Queries.ObtenerDocumentacionVisita;
using CaeManager.Application.Visitas.Queries.ObtenerVisitaPorId;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Visitas;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.QuickGrid;

namespace CaeManager.Web.Features.Visitas.Pages;

public partial class Visitas : ComponentBase
{
    private readonly PaginationState _paginacion = new() { ItemsPerPage = 20 };

    // H2 (docs/ux-audit/02-clientes.md): paginador único en español, ver Clientes.razor.cs.
    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_totalElementos / (double)_paginacion.ItemsPerPage));

    private Task CambiarPaginaAsync(int pagina) => _paginacion.SetCurrentPageIndexAsync(pagina - 1);

    // H5 (docs/ux-audit/05-trabajadores-vehiculos.md): selector de tamaño de página, compartido por PaginadorSimple.razor.
    // Una sola petición: SetCurrentPageIndexAsync ya avisa a QuickGrid aunque la
    // página no cambie, así que refrescar además la rejilla pedía lo mismo dos
    // veces (ver RecargarAsync).
    private Task CambiarTamanoPaginaAsync(int tamano)
    {
        _paginacion.ItemsPerPage = tamano;
        return _paginacion.SetCurrentPageIndexAsync(0);
    }

    private QuickGrid<VisitaListaDto>? _grid;

    private string _busqueda = string.Empty;
    private bool _soloActivas = true;
    private bool _soloUrgentes;
    private string _filtroNotificado = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    private IReadOnlyList<CentroSelectorDto> _centrosDisponibles = [];
    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresDisponibles = [];
    private IReadOnlyList<ElementoSeleccionable> _trabajadoresDisponiblesSelector => _trabajadoresDisponibles
        .Select(t => new ElementoSeleccionable(t.Id, $"{t.NombreCompleto} ({t.Dni})"))
        .ToList();

    private bool _drawerVisible;
    private Guid? _editandoId;
    // Version del registro tal como se abrio: vuelve en el Command para
    // detectar que otra persona guardo mientras el formulario estaba abierto.
    private Guid _versionEditando;
    private string _centroId = string.Empty;
    private string _centroNombreEnEdicion = string.Empty;
    private string _fechaInicio = string.Empty;
    private string _fechaFin = string.Empty;
    private string _horaEstimadaAcceso = string.Empty;
    private HashSet<Guid> _trabajadorIdsSeleccionados = [];
    private bool _notificadoCliente;
    private string _notas = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    // Prellenado desde "Crear visita" de una sugerencia detectada por IA en
    // un correo (ver SugerenciaVisitaCorreo) — se manda de vuelta en
    // CrearVisitaCommand para que el handler marque la sugerencia resuelta.
    private Guid? _sugerenciaVisitaCorreoId;
    private string? _sugerenciaVisitaResumen;

    [SupplyParameterFromQuery(Name = "sugerenciaId")]
    public string? SugerenciaVisitaIdInicial { get; set; }

    // Overrides opcionales del Action Center de Comunicaciones
    // (docs/COMUNICACIONES.md § 12.6): cuando el gestor corrigió Centro o
    // fechas en la revisión previa a confirmar, viajan aquí y prevalecen
    // sobre lo que trae la propia SugerenciaVisitaCorreo almacenada — la
    // corrección "se manda junto con la confirmación", sin persistirse antes.
    [SupplyParameterFromQuery(Name = "centroId")]
    public string? CentroIdOverride { get; set; }

    [SupplyParameterFromQuery(Name = "fechaInicio")]
    public string? FechaInicioOverride { get; set; }

    [SupplyParameterFromQuery(Name = "fechaFin")]
    public string? FechaFinOverride { get; set; }

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _centroAEliminar = string.Empty;
    private bool _eliminando;

    private bool _detalleVisible;
    private bool _cargandoDetalle;
    private DetalleVisitaDto? _detalle;

    /// <summary>
    /// Fila de la lista desde la que se abrió el detalle. Aporta lo que
    /// <see cref="DetalleVisitaDto"/> no trae —urgencia y origen— sin pedir
    /// nada nuevo al servidor; si el detalle se abriera sin fila, esos dos
    /// datos simplemente no se pintan.
    /// </summary>
    private VisitaListaDto? _filaDetalle;

    private bool _marcandoNotificadoDetalle;
    private readonly HashSet<Guid> _marcandoNotificado = [];

    // Contadores de carga vigente. Cada carga que escribe estado tras un
    // await captura el suyo al empezar y descarta su respuesta si, al volver,
    // ya hay otra más reciente: sin esto, una respuesta lenta de un filtro
    // (o de una visita) anterior pisaba la del actual.
    private int _cargaLista;
    private int _cargaDetalle;
    private int _cargaFormulario;
    private int _cargaDocumentacion;

    private bool _cargandoDocumentacion;
    private bool _errorDocumentacion;
    private DocumentacionVisitaDto? _documentacion;

    private bool _visorVisible;
    private Guid _visorDocumentoId;
    private string _visorTitulo = string.Empty;

    private readonly HashSet<Guid> _seleccionados = [];

    /// <summary>
    /// Los checkboxes de fila solo se pintan con esto activo (Centro 360,
    /// PLAN-EJECUCION-UX.md § 0.9) — son ruido permanente para una acción
    /// ocasional. Apagarlo limpia la selección: dejar filas marcadas que ya
    /// no se ven dejaría la barra de acciones en lote apuntando a algo
    /// invisible.
    /// </summary>
    private bool _seleccionMultiple;

    private void AlternarSeleccionMultiple(bool activa)
    {
        _seleccionMultiple = activa;
        if (!activa)
            _seleccionados.Clear();
    }
    private List<VisitaListaDto> _elementosPagina = [];
    private Guid? _idEnfocado;
    private bool _eliminandoLote;
    private bool _confirmarEliminarLoteVisible;

    [SupplyParameterFromQuery(Name = "q")]
    public string? TerminoBusquedaInicial { get; set; }

    [SupplyParameterFromQuery(Name = "notificado")]
    public string? NotificadoInicial { get; set; }

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private GridItemsProvider<VisitaListaDto>? _proveedorElementos;

    // Delegado estable — ver Clientes.razor.cs (bucle de recargas de QuickGrid).
    protected override void OnInitialized() => _proveedorElementos = ProveerElementosAsync;

    /// <summary>
    /// Abre el drawer prellenado si se llegó desde el botón "Crear visita" de
    /// una sugerencia de la Bandeja (?sugerenciaId=...), o desde "Programar
    /// visita" de Centro 360 (?centroId=...&centroNombre=..., sin sugerencia).
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        if (Guid.TryParse(SugerenciaVisitaIdInicial, out var sugerenciaId))
            await AbrirCrearDesdeSugerenciaAsync(sugerenciaId);
        else if (Guid.TryParse(CentroIdOverride, out var centroIdInicial))
            await AbrirCrearParaCentroAsync(centroIdInicial);
    }

    /// <summary>
    /// Se re-ejecuta en cada navegación dentro de la propia página, no solo
    /// en el primer render — la URL como fuente de verdad de los filtros
    /// (P1-18 de docs/business/MATURITY_REVIEW.md).
    /// </summary>
    protected override void OnParametersSet()
    {
        _busqueda = TerminoBusquedaInicial ?? string.Empty;
        _filtroNotificado = NotificadoInicial ?? string.Empty;
    }

    /// <summary>
    /// Proveedor del QuickGrid. Los filtros se capturan en la consulta ANTES
    /// del await, y la respuesta solo escribe estado de la página si sigue
    /// siendo la carga vigente: cambiar dos filtros seguidos lanza dos cargas,
    /// y si la primera vuelve la última su total pisaba el del filtro actual
    /// —el estado vacío «con estos filtros» desaparecía con la lista vacía—.
    /// QuickGrid ya descarta los elementos de una carga superada (cancela su
    /// token al empezar la siguiente); lo que no puede descartar son los
    /// efectos que este método hace por su cuenta.
    /// </summary>
    private async ValueTask<GridItemsProviderResult<VisitaListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<VisitaListaDto> request)
    {
        var carga = ++_cargaLista;
        _cargando = true;
        _errorCarga = false;

        var (ordenarPor, descendente) = LecturaOrden.Leer(request);
        var consulta = new ObtenerVisitasQuery(
            Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
            SoloActivas: _soloActivas,
            NotificadoCliente: _filtroNotificado switch { "si" => true, "no" => false, _ => null },
            SoloUrgentes: _soloUrgentes,
            Pagina: (request.StartIndex / _paginacion.ItemsPerPage) + 1,
            TamanoPagina: _paginacion.ItemsPerPage,
            OrdenarPor: ordenarPor,
            Descendente: descendente);

        try
        {
            var resultado = await Mediator.Send(consulta, request.CancellationToken);
            var elementos = resultado.Elementos.ToList();

            if (carga != _cargaLista)
                return GridItemsProviderResult.From(elementos, resultado.TotalElementos);

            _totalElementos = resultado.TotalElementos;
            _elementosPagina = elementos;
            _seleccionados.Clear();
            _idEnfocado = null;

            return GridItemsProviderResult.From(elementos, resultado.TotalElementos);
        }
        catch (Exception)
        {
            // Una carga superada puede acabar cancelada: eso no es un error
            // de la carga vigente y no puede pintar «No pudimos cargar».
            if (carga == _cargaLista)
                _errorCarga = true;
            return GridItemsProviderResult.From(new List<VisitaListaDto>(), 0);
        }
        finally
        {
            if (carga == _cargaLista)
            {
                _cargando = false;
                StateHasChanged();
            }
        }
    }

    private async Task BuscarAsync(string valor)
    {
        _busqueda = valor;
        NavigationManager.ActualizarFiltroEnUrl("q", valor);
        await RecargarAsync();
    }

    private async Task FiltrarPorNotificadoAsync(string valor)
    {
        _filtroNotificado = valor;
        NavigationManager.ActualizarFiltroEnUrl("notificado", valor);
        await RecargarAsync();
    }

    /// <summary>
    /// Filtros que el USUARIO ha puesto, más allá del que viene de fábrica.
    ///
    /// <para>
    /// <c>_soloActivas</c> queda deliberadamente fuera: nace en <c>true</c>, así
    /// que incluirlo haría esta propiedad siempre cierta y dejaría inalcanzable
    /// el estado de «todavía no hay visitas». Su caso tiene su propio estado
    /// vacío, que además es el único que puede decir la verdad cuando hay
    /// visitas finalizadas detrás.
    /// </para>
    ///
    /// <para>
    /// Desmarcar «Solo activas» tampoco cuenta como filtrar: ensancha la lista,
    /// no la recorta, así que nunca puede ser la causa de que no salga nada.
    /// </para>
    /// </summary>
    private bool HayFiltrosActivos =>
        !string.IsNullOrWhiteSpace(_busqueda) || _soloUrgentes
        || !string.IsNullOrWhiteSpace(_filtroNotificado);

    private async Task LimpiarFiltrosAsync()
    {
        _busqueda = string.Empty;
        _soloUrgentes = false;
        _filtroNotificado = string.Empty;
        // "notificado" también viaja por la URL y OnParametersSet lo
        // re-sincroniza desde ella: dejarlo puesto lo devolvía en la siguiente
        // pasada de parámetros, y "Quitar los filtros" no lo quitaba. Mismo
        // defecto que Clientes y Documentos, encontrado al barrer las pantallas
        // hermanas. Los dos en una sola llamada: varias seguidas se pisan.
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["q"] = null,
            ["notificado"] = null,
        });
        await RecargarAsync();
    }

    /// <summary>Desmarca el filtro de fábrica para que aparezcan las finalizadas.</summary>
    private async Task VerTambienFinalizadasAsync()
    {
        _soloActivas = false;
        await RecargarAsync();
    }

    /// <summary>
    /// Vuelve a la página 1 y pide la lista UNA vez.
    /// <see cref="PaginationState.SetCurrentPageIndexAsync"/> no lleva guarda de
    /// igualdad: avisa a QuickGrid cambie o no la página, y QuickGrid recarga al
    /// recibir el aviso. Llamar además a <c>RefreshDataAsync</c> pedía dos veces
    /// lo mismo. Ver <c>Clientes.razor.cs</c> para el detalle del componente.
    /// </summary>
    private async Task RecargarAsync()
    {
        if (_grid is not null && _paginacion.CurrentPageIndex == 0)
            await _grid.RefreshDataAsync();
        else
            await _paginacion.SetCurrentPageIndexAsync(0);

        StateHasChanged();
    }

    private Task AbrirCrearAsync() => PrepararCrearAsync();

    /// <returns>
    /// <c>false</c> si mientras se cargaban los selectores se abrió otro
    /// formulario (otra alta o una edición): entonces no se toca nada.
    /// </returns>
    private async Task<bool> PrepararCrearAsync()
    {
        var carga = ++_cargaFormulario;
        var centros = await Mediator.Send(new ObtenerCentrosParaSelectorQuery());
        var trabajadores = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery());
        if (carga != _cargaFormulario)
            return false;

        _centrosDisponibles = centros;
        _trabajadoresDisponibles = trabajadores;
        _editandoId = null;
        _centroId = string.Empty;
        _centroNombreEnEdicion = string.Empty;
        _fechaInicio = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        _fechaFin = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        _horaEstimadaAcceso = string.Empty;
        _trabajadorIdsSeleccionados = [];
        _notificadoCliente = false;
        _notas = string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _sugerenciaVisitaCorreoId = null;
        _sugerenciaVisitaResumen = null;
        _drawerVisible = true;
        return true;
    }

    /// <summary>Variante de AbrirCrearAsync que prellena Centro/fechas/notas con lo que detectó la IA en un correo — el Gestor sigue teniendo que elegir los trabajadores y confirmar el resto a mano.</summary>
    private async Task AbrirCrearDesdeSugerenciaAsync(Guid sugerenciaId)
    {
        var carga = ++_cargaFormulario;
        var sugerencia = await Mediator.Send(new ObtenerSugerenciaVisitaCorreoQuery(sugerenciaId));
        if (carga != _cargaFormulario)
            return;

        if (sugerencia is null)
        {
            ToastService.Mostrar("No encontramos esta sugerencia. Puede que ya se haya resuelto.", TonoToast.Error);
            return;
        }

        if (!await PrepararCrearAsync())
            return;

        _sugerenciaVisitaCorreoId = sugerencia.Id;
        _sugerenciaVisitaResumen = sugerencia.Resumen;

        _centroId = Guid.TryParse(CentroIdOverride, out var centroIdCorregido)
            && _centrosDisponibles.Any(c => c.Id == centroIdCorregido)
            ? centroIdCorregido.ToString()
            : sugerencia.CentroId?.ToString() ?? string.Empty;

        if (DateOnly.TryParse(FechaInicioOverride, out var fechaInicioCorregida))
            _fechaInicio = fechaInicioCorregida.ToString("yyyy-MM-dd");
        else if (sugerencia.FechaInicio is not null)
            _fechaInicio = sugerencia.FechaInicio.Value.ToString("yyyy-MM-dd");

        if (DateOnly.TryParse(FechaFinOverride, out var fechaFinCorregida))
            _fechaFin = fechaFinCorregida.ToString("yyyy-MM-dd");
        else if (sugerencia.FechaFin is not null)
            _fechaFin = sugerencia.FechaFin.Value.ToString("yyyy-MM-dd");
    }

    /// <summary>Variante de AbrirCrearAsync para "Programar visita" desde Centro 360: mismo drawer, con el Centro ya elegido en el CampoSelect — el Gestor solo pone fechas y trabajadores.</summary>
    private async Task AbrirCrearParaCentroAsync(Guid centroId)
    {
        if (await PrepararCrearAsync() && _centrosDisponibles.Any(c => c.Id == centroId))
            _centroId = centroId.ToString();
    }

    private async Task AbrirEditarAsync(Guid id)
    {
        var carga = ++_cargaFormulario;
        var trabajadores = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery());
        var visita = await Mediator.Send(new ObtenerVisitaPorIdQuery(id));

        // Si mientras tanto se abrió otro formulario, esta respuesta ya no es
        // la que el usuario espera ver: rellenar el drawer con ella mezclaría
        // dos visitas en el mismo formulario.
        if (carga != _cargaFormulario)
            return;

        _sugerenciaVisitaCorreoId = null;
        _sugerenciaVisitaResumen = null;
        _trabajadoresDisponibles = trabajadores;

        if (visita is null)
        {
            ToastService.Mostrar("No encontramos esta visita. Puede que ya se haya eliminado.", TonoToast.Error);
            await RecargarAsync();
            return;
        }

        _editandoId = visita.Id;
        _versionEditando = visita.Version;
        _centroId = visita.CentroId.ToString();
        _centroNombreEnEdicion = $"{visita.CentroNombre} ({visita.ClienteRazonSocial} — {visita.EmpresaRazonSocial})";
        _fechaInicio = visita.FechaInicio.ToString("yyyy-MM-dd");
        _fechaFin = visita.FechaFin.ToString("yyyy-MM-dd");
        _horaEstimadaAcceso = visita.HoraEstimadaAcceso?.ToString("HH:mm") ?? string.Empty;
        _trabajadorIdsSeleccionados = visita.TrabajadorIds.ToHashSet();
        _notificadoCliente = visita.NotificadoCliente;
        _notas = visita.Notas ?? string.Empty;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    /// <summary>
    /// Vista de solo lectura — quién entra y el estado de su documentación.
    /// A diferencia de la versión anterior (PestanaDocumentacion, sin
    /// filtrar por Centro), ObtenerDocumentacionVisitaQuery solo trae lo que
    /// aplica al Centro de esta visita, incluye "Faltante" y viene ordenada
    /// por severidad — ver el comentario de esa Query.
    /// </summary>
    private async Task AbrirDetalleAsync(Guid id)
    {
        // Abrir otra visita mientras la anterior aún carga: la respuesta que
        // llegue tarde no puede pintar la visita equivocada en el drawer.
        var carga = ++_cargaDetalle;
        ++_cargaDocumentacion;
        _filaDetalle = _elementosPagina.FirstOrDefault(e => e.Id == id);
        _detalleVisible = true;
        _cargandoDetalle = true;
        _detalle = null;
        _documentacion = null;
        _errorDocumentacion = false;

        try
        {
            var detalle = await Mediator.Send(new ObtenerDetalleVisitaQuery(id));
            if (carga != _cargaDetalle)
                return;

            _detalle = detalle;
            if (detalle is null)
            {
                ToastService.Mostrar("No encontramos esta visita. Puede que ya se haya eliminado.", TonoToast.Error);
                return;
            }

            await CargarDocumentacionAsync(id);
        }
        catch (Exception)
        {
            if (carga == _cargaDetalle)
                ToastService.Mostrar("No pudimos cargar el detalle de la visita. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            if (carga == _cargaDetalle)
                _cargandoDetalle = false;
        }
    }

    private Task ReintentarDocumentacionAsync() =>
        _detalle is { } detalle ? CargarDocumentacionAsync(detalle.Id) : Task.CompletedTask;

    private async Task CargarDocumentacionAsync(Guid visitaId)
    {
        var carga = ++_cargaDocumentacion;
        _cargandoDocumentacion = true;
        _errorDocumentacion = false;
        _documentacion = null;

        try
        {
            var documentacion = await Mediator.Send(new ObtenerDocumentacionVisitaQuery(visitaId));
            if (carga == _cargaDocumentacion)
                _documentacion = documentacion;
        }
        catch (Exception)
        {
            if (carga == _cargaDocumentacion)
                _errorDocumentacion = true;
        }
        finally
        {
            if (carga == _cargaDocumentacion)
                _cargandoDocumentacion = false;
        }
    }

    /// <summary>
    /// «Comprobación previa» del mockup, derivada solo de lo que ya trae
    /// <see cref="ObtenerDocumentacionVisitaQuery"/>: un trabajador tiene algo
    /// pendiente cuando el peor estado de su sección no es Vigente (la query
    /// devuelve Vigente precisamente cuando no hay nada que señalar). No dice
    /// «no puede entrar»: la pantalla no sabe qué decide el control de acceso
    /// del centro, solo el estado de los documentos.
    /// </summary>
    private static string? ResumenComprobacion(DocumentacionVisitaDto documentacion)
    {
        var total = documentacion.Trabajadores.Count;
        if (total == 0)
            return null;

        var pendientes = documentacion.Trabajadores.Count(t => t.Documentacion.PeorEstado != EstadoDocumento.Vigente);

        if (pendientes == 0)
            return total == 1
                ? "El trabajador que entra no tiene documentación pendiente para este centro."
                : $"Ninguno de los {total} trabajadores que entran tiene documentación pendiente para este centro.";

        return pendientes == 1
            ? $"1 de {total} trabajadores tiene documentación pendiente para este centro."
            : $"{pendientes} de {total} trabajadores tienen documentación pendiente para este centro.";
    }

    private static readonly IReadOnlyDictionary<EstadoDocumento, int> SeveridadTrabajador = new Dictionary<EstadoDocumento, int>
    {
        [EstadoDocumento.Faltante] = 0,
        [EstadoDocumento.Vencido] = 1,
        [EstadoDocumento.Urgente] = 2,
        [EstadoDocumento.Proximo] = 3,
        [EstadoDocumento.Vigente] = 4,
        [EstadoDocumento.SinCaducidad] = 5,
    };

    /// <summary>
    /// Primero quien no está en regla, del peor estado al mejor; dentro del
    /// mismo estado se respeta el orden por apellidos que trae la query
    /// (OrderBy es estable). La query ordena por nombre; el mockup pide esto.
    /// </summary>
    private static IEnumerable<TrabajadorDocumentacionDto> TrabajadoresPorSeveridad(DocumentacionVisitaDto documentacion) =>
        documentacion.Trabajadores.OrderBy(t => SeveridadTrabajador.GetValueOrDefault(t.Documentacion.PeorEstado, 0));

    /// <summary>
    /// Cambia la marca desde el pie del detalle. Mismo comando que el
    /// interruptor de la fila; tras el éxito se recarga la lista para que la
    /// fila refleje lo mismo que el detalle.
    /// </summary>
    private async Task AlternarNotificadoDesdeDetalleAsync()
    {
        if (_detalle is not { } detalle || _marcandoNotificadoDetalle)
            return;

        var notificado = !detalle.NotificadoCliente;
        _marcandoNotificadoDetalle = true;

        try
        {
            var resultado = await Mediator.Send(new MarcarNotificadoClienteCommand(detalle.Id, notificado));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                await RecargarAsync();
                if (_detalle?.Id == detalle.Id)
                    await AbrirDetalleAsync(detalle.Id);
                return;
            }

            // Si mientras tanto se abrió otra visita, su detalle no se toca.
            if (_detalle?.Id == detalle.Id)
                _detalle = _detalle with { NotificadoCliente = notificado };

            await RecargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos actualizar el estado de notificación. Intenta nuevamente.", TonoToast.Error);
            await RecargarAsync();
            if (_detalle?.Id == detalle.Id)
                await AbrirDetalleAsync(detalle.Id);
        }
        finally
        {
            _marcandoNotificadoDetalle = false;
        }
    }

    private async Task EditarDesdeDetalleAsync()
    {
        if (_detalle is not { } detalle)
            return;

        _detalleVisible = false;
        await AbrirEditarAsync(detalle.Id);
    }

    private static string TextoFechas(DateOnly inicio, DateOnly fin) =>
        inicio == fin ? inicio.ToString("dd/MM/yyyy") : $"{inicio:dd/MM/yyyy} – {fin:dd/MM/yyyy}";

    private static string TextoOrigen(OrigenVisita origen) => origen switch
    {
        OrigenVisita.Correo => "Correo",
        OrigenVisita.WhatsApp => "WhatsApp",
        _ => "Plataforma"
    };

    private static TonoBadge TonoOrigen(OrigenVisita origen) => origen switch
    {
        OrigenVisita.Correo => TonoBadge.Info,
        OrigenVisita.WhatsApp => TonoBadge.Exito,
        _ => TonoBadge.Neutro
    };

    private string TextoRecuento => _totalElementos == 1 ? "1 visita" : $"{_totalElementos} visitas";

    /// <summary>
    /// Un Documento existente abre el visor inline. Un hueco "Faltante" lleva
    /// directo al alta manual con el propietario y el tipo ya elegidos — ver
    /// AbrirCrearParaFaltanteAsync/AbrirCrearParaFaltanteEmpresaAsync en
    /// Documentos.razor.cs. Un Documento sin ArchivoUrl (se puede dar de alta
    /// sin adjuntar archivo, ver CrearDocumentoCommand) tampoco tiene nada
    /// que previsualizar — va directo a editar en vez de abrir un visor vacío.
    /// </summary>
    private void AbrirDocumento(DocumentoVisitaItemDto item)
    {
        if (item.DocumentoId is { } documentoId)
        {
            if (item.ArchivoUrl is null)
            {
                NavigationManager.NavigateTo($"/documentos?documentoId={documentoId}");
                return;
            }

            _visorDocumentoId = documentoId;
            _visorTitulo = item.TipoDocumentoNombre;
            _visorVisible = true;
            return;
        }

        if (item.TrabajadorId is { } trabajadorId)
        {
            NavigationManager.NavigateTo($"/documentos?trabajadorId={trabajadorId}&tipoDocumentoId={item.TipoDocumentoId}");
            return;
        }

        NavigationManager.NavigateTo($"/documentos?empresaIdFaltante={_documentacion!.EmpresaId}&tipoDocumentoId={item.TipoDocumentoId}");
    }

    private void AlternarTrabajador(Guid trabajadorId, bool seleccionado)
    {
        if (seleccionado)
            _trabajadorIdsSeleccionados.Add(trabajadorId);
        else
            _trabajadorIdsSeleccionados.Remove(trabajadorId);
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
            if (!DateOnly.TryParse(_fechaInicio, out var fechaInicio) || !DateOnly.TryParse(_fechaFin, out var fechaFin))
            {
                _mensajeErrorFormulario = "Las fechas no son válidas.";
                return;
            }

            var notas = string.IsNullOrWhiteSpace(_notas) ? null : _notas;
            TimeOnly? horaEstimada = TimeOnly.TryParse(_horaEstimadaAcceso, out var hora) ? hora : null;
            var trabajadorIds = _trabajadorIdsSeleccionados.ToList();
            string? mensajeError;

            if (_editandoId is null)
            {
                if (!Guid.TryParse(_centroId, out var centroId))
                {
                    _mensajeErrorFormulario = "Selecciona un centro.";
                    return;
                }

                var resultado = await Mediator.Send(new CrearVisitaCommand(centroId, fechaInicio, fechaFin, trabajadorIds, notas, _sugerenciaVisitaCorreoId, horaEstimada));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }
            else
            {
                var resultado = await Mediator.Send(new EditarVisitaCommand(_editandoId.Value, fechaInicio, fechaFin, trabajadorIds, notas, _versionEditando, horaEstimada));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }

            if (mensajeError is not null)
            {
                _mensajeErrorFormulario = mensajeError;
                return;
            }

            ToastService.Mostrar(
                _editandoId is null ? "Visita creada correctamente." : "Visita actualizada correctamente.",
                TonoToast.Exito);

            _drawerVisible = false;
            await RecargarAsync();
        }
        catch (ValidationException ex)
        {
            _erroresCampo = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _mensajeErrorFormulario = "No pudimos guardar los cambios. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardando = false;
        }
    }

    private string? ObtenerError(string campo) => _erroresCampo.GetValueOrDefault(campo);

    private async Task AlternarNotificadoAsync(Guid id, bool notificado)
    {
        if (!_marcandoNotificado.Add(id))
            return;

        try
        {
            var resultado = await Mediator.Send(new MarcarNotificadoClienteCommand(id, notificado));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                await RecargarAsync();
                return;
            }

            await RecargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos actualizar el estado de notificación. Intenta nuevamente.", TonoToast.Error);
            await RecargarAsync();
        }
        finally
        {
            _marcandoNotificado.Remove(id);
        }
    }

    private void AbrirEliminar(Guid id, string centroNombre)
    {
        _idAEliminar = id;
        _centroAEliminar = centroNombre;
        _confirmarEliminarVisible = true;
    }

    private async Task ConfirmarEliminarAsync()
    {
        _eliminando = true;

        try
        {
            var resultado = await Mediator.Send(new EliminarVisitaCommand(_idAEliminar));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                ToastService.Mostrar("Visita eliminada correctamente.", TonoToast.Exito);
                _confirmarEliminarVisible = false;
                await RecargarAsync();
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar la visita. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }

    private bool TodosSeleccionados =>
        _elementosPagina.Count > 0 && _elementosPagina.All(e => _seleccionados.Contains(e.Id));

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

    private async Task ConfirmarEliminarLoteAsync()
    {
        _eliminandoLote = true;

        try
        {
            var resultado = await Mediator.Send(new EliminarVisitasCommand(_seleccionados.ToList()));
            var dto = resultado.Valor;

            ToastService.Mostrar(
                dto.Errores.Count == 0
                    ? $"{dto.Eliminados} visita(s) eliminada(s)."
                    : $"{dto.Eliminados} eliminada(s). {dto.Errores.Count} no se pudieron borrar: {string.Join(" ", dto.Errores)}",
                dto.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);

            _seleccionados.Clear();
            _confirmarEliminarLoteVisible = false;
            await RecargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar las visitas seleccionadas. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _eliminandoLote = false;
        }
    }

    private string ObtenerClaseFila(VisitaListaDto item) => item.Id == _idEnfocado ? "fila-enfocada" : "";

    private async Task ManejarAtajoAsync(string tecla)
    {
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
                if (_idEnfocado is { } idAlternar)
                    AlternarSeleccion(idAlternar, !_seleccionados.Contains(idAlternar));
                break;
            case "Enter":
                if (_idEnfocado is { } idAbrir)
                    await AbrirDetalleAsync(idAbrir);
                break;
        }

        StateHasChanged();
    }
}
