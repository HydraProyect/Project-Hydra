using System.Text.Json;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Commands.EliminarDocumento;
using CaeManager.Application.Documentos.Commands.EliminarDocumentos;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Application.Documentos.Commands.RestaurarDocumento;
using CaeManager.Application.Documentos.Queries.DetectarCamposDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectosParaSelector;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Commands.AsignarAliasTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculosParaSelector;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Documentos;
using CaeManager.Web.Features.Documentos.Components;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.QuickGrid;
using Microsoft.Extensions.Logging;

namespace CaeManager.Web.Features.Documentos.Pages;

public partial class Documentos : ComponentBase, IDisposable
{
    /// <summary>
    /// Plataforma, Reclamaciones, Revisión IA y Plantillas son pestañas de
    /// gestión interna (acreditaciones, reclamaciones, revisión y aplicación
    /// de lecturas IA, generación de documentos desde plantilla) — todas
    /// despachan Commands. La página en sí no restringe rol (Documentos
    /// también es la superficie de lectura del rol Cliente, ver NavMenu.razor),
    /// y Pestanas no autoriza por pestaña hoy, así que cada rama de contenido
    /// se protege aquí para que el rol Cliente (y Consulta, que tampoco
    /// escribe — ver AutorizacionEscrituraBehavior) no llegue ni a ver el
    /// formulario, aunque el Command ya lo rechazaría igual.
    /// </summary>
    private const string RolesDeGestionDocumental =
        $"{CaeManager.Infrastructure.Identity.Roles.Administrador},{CaeManager.Infrastructure.Identity.Roles.DireccionCae}," +
        $"{CaeManager.Infrastructure.Identity.Roles.CoordinadorCae},{CaeManager.Infrastructure.Identity.Roles.GestorCae}";

    /// <summary>
    /// Permite llegar aquí desde Alertas o Calendario con un documento
    /// concreto ya listo para gestionar (p. ej. "/documentos?documentoId=...")
    /// en vez de obligar a buscarlo manualmente en la lista.
    /// </summary>
    [SupplyParameterFromQuery] public Guid? DocumentoId { get; set; }

    /// <summary>
    /// Permite llegar aquí desde el Dashboard con el filtro de Estado ya
    /// aplicado (p. ej. la tarjeta KPI "Vigentes" enlaza a
    /// "/documentos?estado=Vigente").
    /// </summary>
    [SupplyParameterFromQuery] public string? Estado { get; set; }

    /// <summary>
    /// Permite llegar aquí desde Alertas con un documento "faltante" (P1-15
    /// de docs/business/MATURITY_REVIEW.md — Trabajador con Asignación activa
    /// a un Centro que exige un TipoDocumento obligatorio, sin ningún
    /// Documento de ese tipo): abre el drawer de creación con el propietario
    /// y el tipo ya elegidos, en vez de "gestionar" un Documento que todavía
    /// no existe (DocumentoId, arriba, siempre es null en este caso).
    /// </summary>
    [SupplyParameterFromQuery] public Guid? TrabajadorId { get; set; }

    /// <summary>Misma idea que <see cref="TrabajadorId"/> pero para un documento "faltante" de Ámbito Empresa (ver Detalle de la visita).</summary>
    [SupplyParameterFromQuery] public Guid? EmpresaIdFaltante { get; set; }

    [SupplyParameterFromQuery] public Guid? TipoDocumentoId { get; set; }

    [SupplyParameterFromQuery(Name = "q")] public string? TerminoBusquedaInicial { get; set; }

    /// <summary>Ámbito del filtro de la rejilla — mismo mecanismo que <see cref="Estado"/>, ver OnParametersSet.</summary>
    [SupplyParameterFromQuery] public string? Ambito { get; set; }

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    /// <summary>Comando del palette "Crear documento" (P3-31): /documentos?accion=crear abre el Drawer directamente.</summary>
    [SupplyParameterFromQuery] public string? Accion { get; set; }

    /// <summary>Pestaña con la que abrir la página — deep-link desde otras superficies (hoy, el timeline de Comunicaciones).</summary>
    [SupplyParameterFromQuery] public string? Pestana { get; set; }

    private GridItemsProvider<DocumentoListaDto>? _proveedorElementos;

    /// <summary>
    /// Los Ids son estables y viajan en la URL (<c>?pestana=</c>, deep-link
    /// desde el timeline de Comunicaciones): las etiquetas se alinean al mockup
    /// Gen 2, los Ids NO se tocan.
    ///
    /// <para>
    /// Dos rótulos se apartan del mockup por el contrato terminológico, que
    /// manda sobre el diseño en el vocabulario: «Plataformas CAE» y no
    /// «Por plataforma» —«plataforma» a secas colisiona con el plano Plataforma
    /// del ADR-011, el de quien accede excepcionalmente desde TALVEG, mientras
    /// que aquí se habla de la Plataforma CAE externa (Nalanda, Dokify,
    /// CTAIMA), que es como la nombra el propio dominio
    /// (<c>ProveedorPlataformaCae</c>)—; y «Plantillas», que el mockup no
    /// dibuja pero la pantalla ya tiene: un mockup omite comportamiento, no lo
    /// deroga.
    /// </para>
    ///
    /// <para>
    /// No es estática porque la primera pestaña lleva el recuento del mockup, y
    /// ese número depende de lo cargado.
    /// </para>
    /// </summary>
    private IReadOnlyList<PestanaDefinicion> PestanasDocumentos =>
    [
        new("listado", "Estado", "documentos") { Contador = ContadorDeEstado },
        new("plataforma", "Plataformas CAE", "plataforma"),
        // "correo" y "reloj" son los más cercanos del catálogo: no hay glifo
        // propio de reclamación (que sale por correo) ni de preventivo (que
        // es anticiparse al vencimiento). Si algún día se dibujan, aquí.
        new("reclamaciones", "Reclamaciones", "correo"),
        new("sugerencias", "Preventivo", "reloj"),
        new("revision-ia", "Revisión IA", "ia"),
        new("plantillas", "Plantillas", "plantilla")
    ];

    /// <summary>Ids de pestaña válidos, sin construir la tira entera para comprobar uno.</summary>
    private static readonly string[] _idsDePestana =
        ["listado", "plataforma", "reclamaciones", "sugerencias", "revision-ia", "plantillas"];

    /// <summary>
    /// El recuento de la pestaña «Estado», que el mockup pinta como píldora.
    /// Es el total que YA devuelve <see cref="ObtenerDocumentosQuery"/> —el
    /// filtrado vigente incluido—, no un total inventado: hasta que la lista
    /// responda por primera vez no hay número y la píldora no se pinta, en
    /// lugar de anunciar un cero que nadie ha contado.
    ///
    /// <para>
    /// Un cero medido tampoco se pinta, y esto sí es una DECISIÓN: «Estado 0»
    /// no informa de nada que el estado vacío de la lista no diga mejor, y
    /// añade una píldora permanente a la pestaña más usada. El mockup tampoco
    /// dibuja ninguna con cero.
    /// </para>
    ///
    /// <para>
    /// Las otras cinco pestañas se quedan sin píldora a propósito: sus
    /// recuentos (documentos pendientes por Plataforma CAE, reclamaciones
    /// abiertas, vencimientos de la ventana preventiva, extracciones de IA por
    /// revisar) exigirían una consulta de recuento propia por pestaña que hoy
    /// no existe, y que además se pagaría en cada carga de la página. Ver el
    /// informe del incremento.
    /// </para>
    /// </summary>
    private ContadorPestana? ContadorDeEstado =>
        _totalConocido is not { } total || total == 0
            ? null
            : new ContadorPestana(total, total == 1 ? "documento" : "documentos");

    private string _pestanaActiva = "listado";

    /// <summary>
    /// La pestaña se refleja en la URL (mismo mecanismo que los filtros de la
    /// rejilla, P1-18) para que recargar, compartir el enlace o navegar
    /// atrás/adelante no pierda la sección elegida — hallazgo de revisión
    /// adversarial de Codex tras plegar Revisión IA en pestaña: antes tenía
    /// ruta propia y por tanto URL durable por definición. "listado" no se
    /// escribe en la URL por ser el valor por defecto.
    /// </summary>
    private void CambiarPestana(string pestana)
    {
        _pestanaActiva = pestana;
        CambiarContextoDocumental();
        NavigationManager.ActualizarFiltroEnUrl(nameof(Pestana), pestana == "listado" ? null : pestana);
    }

    // --- Ciclo de vida y contexto en pantalla ---------------------------------------------------

    /// <summary>
    /// Se cancela al retirarse la página: las consultas en curso dejan de
    /// trabajar para nadie y ninguna respuesta tardía repinta un componente ya
    /// desechado. Mismo patrón que <c>Empresas.razor.cs</c>.
    ///
    /// <para>
    /// Su <c>Token</c> se lee SIEMPRE antes del primer <c>await</c> del método
    /// que lo usa. Leerlo después deja que un <see cref="Dispose"/> intermedio
    /// lo haya desechado, y la lectura lanzaría <see cref="ObjectDisposedException"/>
    /// en una continuación donde nadie la recoge.
    /// </para>
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    /// <summary>
    /// Número de la última carga de la lista. Cada carga captura el suyo ANTES
    /// del <c>await</c> y, al volver, solo escribe estado si sigue siendo la
    /// vigente: sin esto, la respuesta de un filtro ya abandonado pisaba el
    /// total, las filas y la selección de la pregunta que sí se está mirando.
    /// </summary>
    private int _cargaVigente;

    /// <summary>
    /// Qué documentos hay en pantalla: cambia al cambiar de pestaña o de
    /// cualquiera de los tres filtros. Lo que una modal, una selección o un
    /// formulario tuvieran preparado pertenecía al contexto anterior y deja de
    /// valer — las modales y la barra de lote se pintan FUERA del bloque de
    /// pestañas, así que sin esto sobreviven al cambio y se ejecutan sobre lo
    /// que ya no se ve.
    ///
    /// <para>
    /// Es un número, y no un booleano, porque también decide si el
    /// <c>finally</c> de una escritura puede apagar su bandera de «en curso»:
    /// la escritura anterior, al terminar, apagaría la bandera de la que
    /// empezó después y la reabriría a un segundo envío.
    /// </para>
    /// </summary>
    private int _contextoVigente;

    /// <summary>
    /// Firma del contexto tal y como se vio la última vez, para distinguir
    /// «los parámetros se volvieron a evaluar» de «cambió lo que hay en
    /// pantalla». <c>null</c> es la primera pasada, que no cierra nada: no hay
    /// nada anterior a lo que pudiera pertenecer lo abierto.
    /// </summary>
    private string? _contextoEnPantalla;

    public void Dispose()
    {
        if (_desechado)
            return;

        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    /// <summary>La respuesta es de la pregunta vigente y la página sigue viva.</summary>
    private bool EsVigente(int carga) => !_desechado && carga == _cargaVigente;

    /// <summary>Lo preparado sigue perteneciendo a lo que hay en pantalla.</summary>
    private bool ContextoSigueSiendo(int contexto) => !_desechado && contexto == _contextoVigente;

    /// <summary>
    /// Cambió lo que hay en pantalla: se cierra todo lo que quedara abierto y
    /// se tira lo que tuviera preparado. No basta con cerrar la modal — hay que
    /// soltar además el objetivo que guardaba, o volver a abrirla mostraría el
    /// documento de antes.
    /// </summary>
    private void CambiarContextoDocumental()
    {
        _contextoVigente++;

        _confirmarEliminarVisible = false;
        _idAEliminar = Guid.Empty;
        _propietarioAEliminar = string.Empty;
        _tipoDocumentoAEliminar = string.Empty;

        _confirmarEliminarLoteVisible = false;
        _seleccionados.Clear();

        _mostrarGuardarFiltro = false;
        _nombreFiltroNuevo = string.Empty;

        // Las banderas de «en curso» se reinician porque pertenecían a lo
        // anterior: dejarlas encendidas bloquearía para siempre el botón del
        // contexto nuevo. Su contrapartida está en cada finally, que solo
        // apaga la suya si el contexto sigue siendo el que la encendió.
        _eliminando = false;
        _eliminandoLote = false;
        _guardandoFiltro = false;
    }

    // --- P3-31: selección múltiple, atajos j/k, filtros guardados ---
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
    private List<DocumentoListaDto> _elementosPagina = [];
    private Guid? _idEnfocado;
    private bool _eliminandoLote;
    private bool _confirmarEliminarLoteVisible;

    private IReadOnlyList<FiltroGuardadoDto> _filtrosGuardados = [];
    private bool _mostrarGuardarFiltro;
    private string _nombreFiltroNuevo = string.Empty;
    private bool _guardandoFiltro;

    private record FiltrosDocumentosJson(string? Busqueda, string? Ambito, string? Estado);

    private DrawerGestionDocumento _drawerGestion = default!;

    protected override async Task OnInitializedAsync()
    {
        // Delegado estable — ver Clientes.razor.cs (bucle de recargas de QuickGrid).
        _proveedorElementos = ProveerElementosAsync;

        var token = _ciclo.Token;
        _filtrosGuardados = await Mediator.Send(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Documentos), token);
    }

    /// <summary>
    /// _drawerGestion (@ref) todavía no está asignado durante OnInitializedAsync
    /// — Blazor lo rellena tras el primer render del árbol de componentes. La
    /// apertura automática por query string (deep-link desde Alertas/Calendario/
    /// Dashboard/palette) tiene que esperar a este punto.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;

        if (DocumentoId is not null)
            await _drawerGestion.AbrirEditarAsync(DocumentoId.Value);
        else if (TrabajadorId is not null && TipoDocumentoId is not null)
            await _drawerGestion.AbrirCrearParaFaltanteAsync(TrabajadorId.Value, TipoDocumentoId.Value);
        else if (EmpresaIdFaltante is not null && TipoDocumentoId is not null)
            await _drawerGestion.AbrirCrearParaFaltanteEmpresaAsync(EmpresaIdFaltante.Value, TipoDocumentoId.Value);
        else if (Accion == "crear")
            await _drawerGestion.AbrirCrearAsync();
        else
            return;

        StateHasChanged();
    }

    private Task ManejarDocumentoGuardadoAsync() => RecargarAsync();

    /// <summary>
    /// Se re-ejecuta en cada navegación dentro de la propia página (recargar,
    /// compartir la URL, volver atrás) — no solo en el primer render — para
    /// que la URL sea la fuente de verdad de los tres filtros de la rejilla,
    /// no solo su semilla inicial (P1-18 de docs/business/MATURITY_REVIEW.md).
    /// </summary>
    protected override void OnParametersSet()
    {
        _estadoFiltro = !string.IsNullOrWhiteSpace(Estado) && Enum.TryParse<EstadoDocumento>(Estado, out _)
            ? Estado
            : string.Empty;
        _busqueda = TerminoBusquedaInicial ?? string.Empty;
        _ambitoFiltro = Ambito ?? string.Empty;

        // Deep-link de pestaña: lo usa el timeline de Comunicaciones para llevar
        // desde el evento de reclamación enviada a su pestaña. Se ignora un
        // valor que no exista en vez de dejar la página en blanco.
        if (!string.IsNullOrWhiteSpace(Pestana) && _idsDePestana.Contains(Pestana))
            _pestanaActiva = Pestana;

        // Los filtros son la fuente de verdad de QUÉ hay en pantalla, y viajan
        // por la URL: cambiarlos desde el selector, desde otra pantalla o
        // volviendo atrás pasa siempre por aquí. Si el conjunto cambió, lo que
        // una modal tuviera preparado ya no es de esta pantalla. Va ANTES de
        // "accion=guardar-filtro", que abre su modal a propósito.
        //
        // El separador no es decorativo: concatenando a pelo, mover una letra
        // de un filtro al siguiente daría la misma firma y el cambio pasaría
        // por no-cambio.
        var contexto = string.Join('\n', _pestanaActiva, _busqueda, _ambitoFiltro, _estadoFiltro);
        if (_contextoEnPantalla is null)
            _contextoEnPantalla = contexto;
        else if (_contextoEnPantalla != contexto)
        {
            _contextoEnPantalla = contexto;
            CambiarContextoDocumental();
        }

        // A diferencia de "accion=crear" (OnAfterRenderAsync, solo primer
        // render: siempre llega desde otra página), "guardar-filtro" tiene
        // que funcionar estando YA en /documentos — el propio Command
        // Palette navega a la misma ruta añadiendo el query string, sin
        // recrear el componente. OnParametersSet es el único hook que se
        // re-ejecuta en ese caso, y se ejecuta después de resincronizar los
        // filtros de arriba desde la URL, así que el modal parte de los
        // filtros ya vigentes en pantalla.
        if (Accion == "guardar-filtro")
            _mostrarGuardarFiltro = true;
    }

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

    private QuickGrid<DocumentoListaDto>? _grid;

    private string _busqueda = string.Empty;
    private string _ambitoFiltro = string.Empty;
    private string _estadoFiltro = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    /// <summary>
    /// El total, solo cuando la lista ha respondido de verdad alguna vez. El
    /// cero de <see cref="_totalElementos"/> antes de la primera respuesta no
    /// es un recuento: es la falta de uno, y la píldora de la pestaña no puede
    /// anunciarlo como si lo fuera.
    /// </summary>
    private int? _totalConocido;

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _propietarioAEliminar = string.Empty;
    private string _tipoDocumentoAEliminar = string.Empty;
    private bool _eliminando;

    private async ValueTask<GridItemsProviderResult<DocumentoListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<DocumentoListaDto> request)
    {
        if (_desechado)
            return GridItemsProviderResult.From(new List<DocumentoListaDto>(), 0);

        // Todo lo que define la pregunta —el número de carga y el token— se lee
        // ANTES del await. Leer _ciclo.Token después dejaría que un Dispose
        // intermedio lo hubiera desechado, y la lectura lanzaría
        // ObjectDisposedException en una continuación que nadie observa.
        var carga = ++_cargaVigente;

        // Dos motivos para cancelar, no uno: que la pantalla se retire (el
        // ciclo) y que la rejilla pida otra página, otro orden u otro filtro
        // (QuickGrid). Antes solo viajaba el del ciclo, así que la consulta ya
        // sustituida seguía trabajando en el servidor hasta terminar; su
        // resultado no se pintaba —de eso se encarga _cargaVigente— pero el
        // trabajo se hacía igual.
        using var cancelacion = CancellationTokenSource.CreateLinkedTokenSource(_ciclo.Token, request.CancellationToken);
        var token = cancelacion.Token;

        _cargando = true;
        _errorCarga = false;

        try
        {
            var pagina = (request.StartIndex / _paginacion.ItemsPerPage) + 1;
            var (ordenarPor, descendente) = LecturaOrden.Leer(request);

            var ambitoFiltro = Enum.TryParse<AmbitoAplicacion>(_ambitoFiltro, out var ambito) ? ambito : (AmbitoAplicacion?)null;
            var estadoFiltro = Enum.TryParse<EstadoDocumento>(_estadoFiltro, out var estado) ? estado : (EstadoDocumento?)null;

            var resultado = await Mediator.Send(new ObtenerDocumentosQuery(
                TrabajadorId: null,
                Ambito: ambitoFiltro,
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                Estado: estadoFiltro,
                Pagina: pagina,
                TamanoPagina: _paginacion.ItemsPerPage,
                OrdenarPor: ordenarPor,
                Descendente: descendente), token);

            // La respuesta de un filtro ya abandonado no puede pisar el total,
            // las filas ni la selección de la pregunta que sí se está mirando.
            // QuickGrid descarta por su cuenta el resultado de un provider
            // superado, pero el estado de la página lo escribimos aquí.
            if (!EsVigente(carga))
                return GridItemsProviderResult.From(new List<DocumentoListaDto>(), 0);

            _totalElementos = resultado.TotalElementos;
            _totalConocido = resultado.TotalElementos;

            var elementos = resultado.Elementos.ToList();
            _elementosPagina = elementos;
            _seleccionados.Clear();
            _idEnfocado = null;

            return GridItemsProviderResult.From(elementos, resultado.TotalElementos);
        }
        catch (Exception) when (!EsVigente(carga))
        {
            // Una carga superada que falla no es un error de la vigente: no
            // puede tapar su resultado con el estado de error ni ensuciar el
            // log con un fallo de una pregunta que ya nadie hace.
            return GridItemsProviderResult.From(new List<DocumentoListaDto>(), 0);
        }
        catch (Exception ex)
        {
            // _errorCarga solo pinta un aviso genérico en la rejilla; sin este
            // log, un fallo al cargar la pantalla más usada del producto no
            // deja ningún rastro que permita diagnosticarlo después.
            Logger.LogError(ex, "Error al cargar la rejilla de documentos (StartIndex {StartIndex}).", request.StartIndex);

            _errorCarga = true;
            return GridItemsProviderResult.From(new List<DocumentoListaDto>(), 0);
        }
        finally
        {
            if (EsVigente(carga))
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

    private async Task CambiarAmbitoFiltroAsync(string valor)
    {
        _ambitoFiltro = valor;
        NavigationManager.ActualizarFiltroEnUrl(nameof(Ambito), valor);
        await RecargarAsync();
    }

    private async Task CambiarEstadoFiltroAsync(string valor)
    {
        _estadoFiltro = valor;
        NavigationManager.ActualizarFiltroEnUrl(nameof(Estado), valor);
        await RecargarAsync();
    }

    private bool HayFiltrosActivos =>
        !string.IsNullOrWhiteSpace(_busqueda) || !string.IsNullOrWhiteSpace(_estadoFiltro)
        || !string.IsNullOrWhiteSpace(_ambitoFiltro);

    /// <summary>
    /// Quita los tres filtros, y los tres <b>también de la URL</b>. Hasta ahora
    /// solo se borraba <c>q</c>: <c>Estado</c> y <c>Ambito</c> se quedaban
    /// puestos y <see cref="OnParametersSet"/>, que re-sincroniza desde la URL,
    /// los devolvía en la siguiente pasada de parámetros. Pulsar "Quitar los
    /// filtros" con un estado documental elegido dejaba la lista igual de
    /// recortada. Mismo defecto que tenía Clientes, encontrado por la prueba
    /// por render: el trinquete de fuente solo mira que exista la rama.
    ///
    /// <para>
    /// Los tres van en una sola llamada por la razón que documenta el helper:
    /// cada <c>NavigateTo</c> lee la URL vigente y varias seguidas se pisan.
    /// </para>
    /// </summary>
    private async Task LimpiarFiltrosAsync()
    {
        _busqueda = string.Empty;
        _estadoFiltro = string.Empty;
        _ambitoFiltro = string.Empty;
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["q"] = null,
            [nameof(Estado)] = null,
            [nameof(Ambito)] = null,
        });
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

    private void AbrirEliminar(Guid id, string propietarioNombre, string tipoDocumentoNombre)
    {
        _idAEliminar = id;
        _propietarioAEliminar = propietarioNombre;
        _tipoDocumentoAEliminar = tipoDocumentoNombre;
        _confirmarEliminarVisible = true;
    }

    private async Task ConfirmarEliminarAsync()
    {
        // El botón deshabilitado no basta: el segundo clic ya viajaba cuando se
        // deshabilitó. La guarda va en el método, que es lo que se ejecuta.
        if (_eliminando || _desechado)
            return;

        // La pregunta entera se lee antes del primer await: el id que se borra,
        // el token y el contexto al que pertenece esta operación.
        var idEliminado = _idAEliminar;
        var token = _ciclo.Token;
        var contexto = _contextoVigente;

        if (idEliminado == Guid.Empty)
            return;

        _eliminando = true;

        try
        {
            var resultado = await Mediator.Send(new EliminarDocumentoCommand(idEliminado), token);

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                // El aviso se da siempre: el documento se eliminó de verdad y
                // quien lo pidió tiene que poder deshacerlo. Lo que no puede
                // hacerse es tocar la pantalla si ya es otra —cerrar una modal
                // que abrió otra operación, o recargar una lista que ya no es
                // esta—: el contexto se comprueba DESPUÉS del await, no solo en
                // el finally.
                ToastService.Mostrar("Documento eliminado correctamente.", TonoToast.Exito, "Deshacer", () => DeshacerEliminarAsync(idEliminado));

                if (ContextoSigueSiendo(contexto))
                {
                    _confirmarEliminarVisible = false;
                    await RecargarAsync();
                }
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar el documento. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            // Solo apaga la bandera que ella encendió: si entre medias cambió lo
            // que hay en pantalla, la bandera vigente es de otra operación y
            // apagarla la reabriría a un segundo envío.
            if (ContextoSigueSiendo(contexto))
                _eliminando = false;
        }
    }

    /// <summary>
    /// Fase D ("Deshacer al eliminar") — acción del toast tras eliminar, ver
    /// RestaurarDocumentoCommand. Con guarda propia: el aviso sigue en pantalla
    /// mientras se restaura y dos pulsaciones mandarían dos restauraciones.
    /// </summary>
    private readonly HashSet<Guid> _restaurando = [];

    private async Task DeshacerEliminarAsync(Guid id)
    {
        if (_desechado || !_restaurando.Add(id))
            return;

        var token = _ciclo.Token;

        try
        {
            var resultado = await Mediator.Send(new RestaurarDocumentoCommand(id), token);

            ToastService.Mostrar(
                resultado.EsExitoso ? "Documento restaurado." : resultado.Error.Mensaje,
                resultado.EsExitoso ? TonoToast.Exito : TonoToast.Error);

            if (resultado.EsExitoso)
                await RecargarAsync();
        }
        finally
        {
            _restaurando.Remove(id);
        }
    }

    // --- P3-31: selección múltiple ---

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
        if (_eliminandoLote || _desechado)
            return;

        // Se lee todo antes del await, y el número de pedidos se guarda: sin él
        // no hay forma de distinguir después «hizo todo lo pedido» de «hizo
        // parte», porque el DTO solo dice cuántos borró.
        var pedidos = _seleccionados.ToList();
        var token = _ciclo.Token;
        var contexto = _contextoVigente;

        // Una selección vacía no se manda: el validador la rechazaría y el
        // fallo acabaría presentado como «no pudimos eliminar», que sugiere una
        // avería donde solo había un lote que se quedó sin filas —p. ej. porque
        // la lista se recargó con la modal abierta.
        if (pedidos.Count == 0)
        {
            _confirmarEliminarLoteVisible = false;
            return;
        }

        _eliminandoLote = true;

        try
        {
            var resultado = await Mediator.Send(new EliminarDocumentosCommand(pedidos), token);

            // Un Result fallido no traía DTO: leer .Valor sin mirar esto
            // reventaba, y la excepción acababa en el catch de abajo con un
            // texto genérico que se comía el motivo que dio el servidor.
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            var dto = resultado.Valor;
            var completo = dto.Eliminados == pedidos.Count && dto.Errores.Count == 0;

            // Tres desenlaces, no dos. Un lote que borró MENOS de lo pedido no
            // es un éxito aunque no traiga ni un error: la lista de errores no
            // es la medida de lo hecho, el recuento sí. Y cero borrados no es
            // un logro que anunciar en tono de éxito.
            ToastService.Mostrar(
                completo
                    ? $"{dto.Eliminados} documento(s) eliminado(s)."
                    : dto.Eliminados == 0
                        ? $"No se eliminó ningún documento de los {pedidos.Count} seleccionados.{DetalleDeErrores(dto.Errores)}"
                        : $"{dto.Eliminados} de {pedidos.Count} eliminado(s); el resto sigue en la lista.{DetalleDeErrores(dto.Errores)}",
                completo ? TonoToast.Exito : dto.Eliminados == 0 ? TonoToast.Error : TonoToast.Advertencia);

            // Ver el comentario del borrado individual. Aquí además se
            // vaciaba la selección, que al cambiar de contexto ya es la que
            // acaba de hacer quien está mirando ahora.
            if (ContextoSigueSiendo(contexto))
            {
                _seleccionados.Clear();
                _confirmarEliminarLoteVisible = false;
                await RecargarAsync();
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar los documentos seleccionados. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            if (ContextoSigueSiendo(contexto))
                _eliminandoLote = false;
        }
    }

    /// <summary>
    /// Los motivos que dio el servidor, si los dio. El handler devuelve mensajes
    /// ya redactados y sin Id, así que no se pueden atribuir a una fila
    /// concreta: se muestran tal cual, que es más de lo que dice callarlos.
    /// </summary>
    private static string DetalleDeErrores(IReadOnlyList<string> errores) =>
        errores.Count == 0 ? string.Empty : " " + string.Join(" ", errores);

    // --- P3-31: atajos de teclado j/k/x/Enter ---

    private string ObtenerClaseFila(DocumentoListaDto item) => item.Id == _idEnfocado ? "fila-enfocada" : "";

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
                {
                    var elemento = _elementosPagina.FirstOrDefault(e => e.Id == idAbrir);
                    if (elemento is not null)
                        await WorkspaceService.AbrirAsync(EntidadWorkspace.Documento, elemento.Id, elemento.TipoDocumentoNombre, "informacion");
                }
                break;
        }

        StateHasChanged();
    }

    // --- P3-31: filtros guardados ---

    private async Task AplicarFiltroGuardadoAsync(string idTexto)
    {
        if (!Guid.TryParse(idTexto, out var id)) return;

        var filtro = _filtrosGuardados.FirstOrDefault(f => f.Id == id);
        if (filtro is null) return;

        var valores = JsonSerializer.Deserialize<FiltrosDocumentosJson>(filtro.ValoresJson);
        if (valores is null) return;

        _busqueda = valores.Busqueda ?? string.Empty;
        _ambitoFiltro = valores.Ambito ?? string.Empty;
        _estadoFiltro = valores.Estado ?? string.Empty;

        // Los tres van también a la URL, y en una sola llamada. Sin esto, el
        // filtro guardado duraba hasta la siguiente pasada de parámetros:
        // OnParametersSet re-sincroniza desde la URL, que seguía con los
        // filtros de antes, y los devolvía encima de lo recién aplicado. Es el
        // mismo defecto que tenía "Quitar los filtros".
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["q"] = valores.Busqueda,
            [nameof(Estado)] = valores.Estado,
            [nameof(Ambito)] = valores.Ambito,
        });
        await RecargarAsync();
    }

    private async Task GuardarFiltroActualAsync()
    {
        // Sin guarda, dos clics —o un clic mientras el botón todavía se estaba
        // deshabilitando— guardaban dos filtros con el mismo nombre.
        if (_guardandoFiltro || _desechado) return;
        if (string.IsNullOrWhiteSpace(_nombreFiltroNuevo)) return;

        var nombre = _nombreFiltroNuevo;
        var token = _ciclo.Token;
        var contexto = _contextoVigente;

        var valoresJson = JsonSerializer.Serialize(new FiltrosDocumentosJson(
            string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
            string.IsNullOrWhiteSpace(_ambitoFiltro) ? null : _ambitoFiltro,
            string.IsNullOrWhiteSpace(_estadoFiltro) ? null : _estadoFiltro));

        _guardandoFiltro = true;

        try
        {
            var resultado = await Mediator.Send(
                new GuardarFiltroCommand(PantallasConFiltrosGuardados.Documentos, nombre, valoresJson), token);

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            var guardados = await Mediator.Send(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Documentos), token);
            ToastService.Mostrar("Filtro guardado.", TonoToast.Exito);

            // Ver el comentario del borrado individual. El nombre a medio
            // escribir y la modal abierta pueden ser ya de otra operación.
            if (ContextoSigueSiendo(contexto))
            {
                _filtrosGuardados = guardados;
                _mostrarGuardarFiltro = false;
                _nombreFiltroNuevo = string.Empty;
            }
        }
        finally
        {
            if (ContextoSigueSiendo(contexto))
                _guardandoFiltro = false;
        }
    }

    private async Task EliminarFiltroGuardadoAsync(Guid id)
    {
        if (_desechado) return;

        var token = _ciclo.Token;

        var resultado = await Mediator.Send(new EliminarFiltroGuardadoCommand(id), token);
        if (resultado.EsFallido)
        {
            ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            return;
        }

        _filtrosGuardados = await Mediator.Send(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Documentos), token);
    }
}
