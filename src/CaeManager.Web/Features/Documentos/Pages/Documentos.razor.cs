using System.Text.Json;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
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
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Documentos;
using CaeManager.Web.Features.Documentos.Components;
using CaeManager.Web.Features.Documentos.Recursos;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using CaeManager.Infrastructure.Identity;
using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.QuickGrid;
using Microsoft.Extensions.Logging;

namespace CaeManager.Web.Features.Documentos.Pages;

public partial class Documentos : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    /// <summary>Quien mira no alcanza nada en este Tenant (<see cref="CaeManager.Web.Features.IncorporacionCartera.Components.VacioSegunAlcance"/>):
    /// sin «+ Nuevo» en cabecera, para no duplicar lo que quizá ya existe fuera de su cartera.</summary>
    private bool _alcanceCero;
    [CascadingParameter] private Task<AuthenticationState>? EstadoAutenticacion { get; set; }
    private bool _puedeEscribir;

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
    /// de Project-Hydra-Negocio/MATURITY_REVIEW.md — Trabajador con Asignación activa
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
    [Inject] private IStringLocalizer<TextosDocumentos> Textos { get; set; } = default!;
    [Inject] private ITenantActual TenantActual { get; set; } = default!;

    /// <summary>Estado 4a del mockup del selector: hay que elegir una empresa de la cartera antes de ver la lista.</summary>
    private bool _sinEmpresaSeleccionada;

    /// <summary>La empresa activa aún no se ha resuelto: se pinta una carga en vez de la lista.</summary>
    private bool _resolviendoEmpresa = true;

    /// <summary>El enlace profundo (query) ya se aplicó o se descartó; solo se aplica una vez, con la empresa resuelta.</summary>
    private bool _enlaceProfundoAplicado;

    /// <summary>Comando del palette "Crear documento" (P3-31): /documentos?accion=crear abre el Drawer directamente.</summary>
    [SupplyParameterFromQuery] public string? Accion { get; set; }

    /// <summary>Pestaña con la que abrir la página — deep-link desde otras superficies (hoy, el timeline de Comunicaciones).</summary>
    [SupplyParameterFromQuery] public string? Pestana { get; set; }

    /// <summary>
    /// Deep-link de la pestaña Plataforma a una acreditación concreta (P0-9b):
    /// «Corregir en {plataforma}» de Mi trabajo y /bandeja llega con
    /// <c>?pestana=plataforma&amp;acreditacionId=</c>, y la pestaña resalta su fila.
    /// </summary>
    [SupplyParameterFromQuery] public Guid? AcreditacionId { get; set; }

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

    /// <summary>
    /// D-32: además de los ids canónicos, la URL acepta el nombre de la pestaña
    /// tal como se lee en pantalla (<c>plataformas</c>, <c>preventivo</c>,
    /// <c>estado</c>, <c>revision</c>), que es lo que se escribe a mano. Un
    /// valor desconocido devuelve null y la pestaña no cambia.
    /// </summary>
    public static string? IdDePestanaDeUrl(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor))
            return null;

        var v = valor.Trim().ToLowerInvariant();
        v = v switch
        {
            "estado" => "listado",
            "plataformas" => "plataforma",
            "preventivo" => "sugerencias",
            "revision" => "revision-ia",
            _ => v
        };
        return _idsDePestana.Contains(v) ? v : null;
    }

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

    // ── La fila se refresca tras guardar en la vista rápida ─────────────────────────────────────
    // El panel vive en MainLayout y guarda sin pasar por esta página: avisa por
    // ContextWorkspaceService.OnEntidadGuardada. Se vuelve a pedir SOLO esa fila y se sustituye
    // en sitio (mismo criterio que Centros.RefrescarCentroAsync): filtros, orden, página,
    // selección, fila enfocada y desplazamiento no se tocan, y la fila permanece aunque el
    // cambio la saque del filtro activo, hasta la siguiente carga. Los recuentos de la franja
    // tampoco se recalculan hasta entonces.

    /// <summary>
    /// La siguiente petición de QuickGrid se sirve de <see cref="_elementosPagina"/> sin consultar:
    /// QuickGrid solo repinta sus filas cuando su proveedor le entrega una página, y una carga
    /// de verdad limpiaría la selección y la fila enfocada. El total no cambia, así que no hay
    /// segunda petición (ver RecargarAsync).
    /// </summary>
    private bool _servirPaginaEnMemoria;

    private void AlGuardarEntidad(EntidadWorkspace tipo, Guid id)
    {
        if (tipo == EntidadWorkspace.Documento)
            _ = InvokeAsync(() => RefrescarFilaAsync(id));
    }

    private async Task RefrescarFilaAsync(Guid id)
    {
        // Con una carga en vuelo no se sustituye nada: la sustitución caería sobre una página que
        // está a punto de cambiar. Hueco conocido: si esa carga leyó antes de que el guardado
        // fuera firme, la fila conserva el dato anterior hasta la siguiente carga.
        if (_desechado || _grid is null || _cargando || _pestanaActiva != "listado" || !_elementosPagina.Any(e => e.Id == id))
            return;

        var carga = _cargaVigente;
        try
        {
            var resultado = await Mediator.Send(new ObtenerDocumentosQuery(TrabajadorId: null, Ambito: null, Busqueda: null, DocumentoId: id), _ciclo.Token);
            var indice = _elementosPagina.FindIndex(e => e.Id == id);
            if (_desechado || _grid is null || _cargando || carga != _cargaVigente || indice < 0
                || resultado.Elementos.FirstOrDefault() is not { } actualizada)
                return;

            _elementosPagina[indice] = actualizada;
            _servirPaginaEnMemoria = true;
            await _grid.RefreshDataAsync();
        }
        catch (Exception)
        {
            // El guardado ya es firme: que falle la relectura no es un error que enseñar. La
            // fila conserva el dato anterior hasta la siguiente carga, como antes de este aviso.
        }
        finally
        {
            _servirPaginaEnMemoria = false;
        }
    }

    public void Dispose()
    {
        if (_desechado)
            return;

        _desechado = true;
        WorkspaceService.OnEntidadGuardada -= AlGuardarEntidad;
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

        _confirmarEliminarLoteVisible = false;
        _seleccionados.Clear();

        _mostrarGuardarFiltro = false;
        _nombreFiltroNuevo = string.Empty;
        _mensajeErrorFiltro = null;

        // Las banderas de «en curso» se reinician porque pertenecían a lo
        // anterior: dejarlas encendidas bloquearía para siempre el botón del
        // contexto nuevo. Su contrapartida está en cada finally, que solo
        // apaga la suya si el contexto sigue siendo el que la encendió.
        _eliminandoLote = false;
        _guardandoFiltro = false;
    }

    // --- P3-31: selección múltiple, atajos j/k, filtros guardados ---
    private readonly HashSet<Guid> _seleccionados = [];

    /// <summary>
    /// Los checkboxes de fila solo se pintan con esto activo (Centro 360,
    /// Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.9) — son ruido permanente para una acción
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
    private FiltroGuardadoDto? _filtroGuardadoAEliminar;
    private bool _eliminandoFiltroGuardado;

    private void CambiarVisibilidadBorradoFiltroGuardado(bool visible)
    {
        if (!visible) _filtroGuardadoAEliminar = null;
    }
    private bool _mostrarGuardarFiltro;
    private string _nombreFiltroNuevo = string.Empty;
    private bool _guardandoFiltro;

    /// <summary>Error del servidor al guardar el filtro: se ve en el aviso fijo del ModalFormulario (D-20), no en un toast que desaparece.</summary>
    private string? _mensajeErrorFiltro;

    private record FiltrosDocumentosJson(string? Busqueda, string? Ambito, string? Estado);

    private DrawerGestionDocumento _drawerGestion = default!;
    private PlataformaTab? _plataformaTab;

    protected override async Task OnInitializedAsync()
    {
        WorkspaceService.OnEntidadGuardada += AlGuardarEntidad;
        if (EstadoAutenticacion is not null)
        {
            var usuario = (await EstadoAutenticacion).User;
            if (_desechado) return;
            _puedeEscribir = Roles.ConEscrituraCsv.Split(',').Any(usuario.IsInRole);
        }

        // Delegado estable — ver Clientes.razor.cs (bucle de recargas de QuickGrid).
        _proveedorElementos = ProveerElementosAsync;

        var token = _ciclo.Token;

        // Hasta resolver la empresa activa no se monta la lista ni sus acciones: con la consulta en
        // vuelo el render saldría con «hay empresa» y lanzaría la carga (y la exportación) del origen.
        try
        {
            var contexto = await ContextoEmpresaActiva.ResolverAsync(Mediator, TenantActual, token);
            _sinEmpresaSeleccionada = contexto.SinSeleccion;
        }
        finally
        {
            _resolviendoEmpresa = false;
        }

        // La página se retiró mientras se resolvía la empresa: nada más que pedir.
        if (_desechado)
            return;

        // Sin empresa elegida no se piden los datos de la organización de origen.
        if (_sinEmpresaSeleccionada)
            return;

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
        if (_desechado) return;
        var grid = _grid;

        // La opción cambia SortBy; ordenar tras el render garantiza que QuickGrid lee la propiedad nueva.
        // Esto precede a la guarda del enlace profundo, que se aplica una sola vez.
        if (_columnaOrdenPendiente is { } pendiente && grid is not null && _pestanaActiva == "listado"
            && !_resolviendoEmpresa && !_sinEmpresaSeleccionada)
        {
            var columna = pendiente == ColumnaOrdenListado.EntidadAsociada ? _columnaEntidadAsociada : _columnaVigencia;
            if (columna is not null)
            {
                _columnaOrdenPendiente = null;
                await grid.SortByColumnAsync(columna, _sentidoOrdenPendiente);
                if (_desechado || !ReferenceEquals(grid, _grid) || _pestanaActiva != "listado") return;
                await grid.HideColumnOptionsAsync();
            }
        }
        // El enlace profundo espera a que la empresa esté resuelta: con el 4a activo no se abre nada
        // (el drawer escribiría en el Tenant de origen), y mientras se resuelve tampoco.
        if (_enlaceProfundoAplicado || _resolviendoEmpresa) return;
        _enlaceProfundoAplicado = true;
        if (_sinEmpresaSeleccionada) return;

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

    /// <summary>
    /// D-32: el cajón abierto por enlace profundo dejaba <c>?documentoId=…</c> en
    /// la URL tras cerrarlo, y recargar lo reabría. Al cerrar (o guardar) se
    /// quitan los parámetros que lo abrieron; solo si estaban, para no navegar en vano.
    /// </summary>
    private void LimpiarEnlaceProfundoDeUrl()
    {
        if (DocumentoId is null && TrabajadorId is null && EmpresaIdFaltante is null && TipoDocumentoId is null && Accion is null)
            return;

        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["documentoId"] = null,
            ["trabajadorId"] = null,
            ["empresaIdFaltante"] = null,
            ["tipoDocumentoId"] = null,
            ["accion"] = null,
        });
    }

    private async Task ManejarDocumentoGuardadoAsync()
    {
        LimpiarEnlaceProfundoDeUrl();

        // La versión corregida devuelve las acreditaciones del Documento a
        // Pendiente de subir: la pestaña Plataforma tiene que enseñarlo ya,
        // no la fila Rechazada que había antes de guardar. La rejilla del
        // listado no está montada en esa pestaña (su @ref sería la de una
        // QuickGrid ya desmontada) y se recarga sola al volver a ella.
        if (_pestanaActiva == "plataforma" && _plataformaTab is not null)
            await _plataformaTab.RecargarAsync();
        else
            await RecargarAsync();
    }

    /// <summary>
    /// «Subir versión corregida» de una acreditación Rechazada: el mismo drawer que
    /// «Renovar» del listado, que guarda con <c>RenovarDocumentoCommand</c>.
    /// </summary>
    private Task AbrirVersionCorregidaAsync(Guid documentoId) => _drawerGestion.AbrirEditarAsync(documentoId);

    /// <summary>
    /// Se re-ejecuta en cada navegación dentro de la propia página (recargar,
    /// compartir la URL, volver atrás) — no solo en el primer render — para
    /// que la URL sea la fuente de verdad de los tres filtros de la rejilla,
    /// no solo su semilla inicial (P1-18 de Project-Hydra-Negocio/MATURITY_REVIEW.md).
    /// </summary>
    protected override void OnParametersSet()
    {
        _estadoFiltro = EstadosValidos(Estado);
        _busqueda = TerminoBusquedaInicial ?? string.Empty;
        _ambitoFiltro = Ambito ?? string.Empty;

        // Deep-link de pestaña: lo usa el timeline de Comunicaciones para llevar
        // desde el evento de reclamación enviada a su pestaña. Se ignora un
        // valor que no exista en vez de dejar la página en blanco.
        if (IdDePestanaDeUrl(Pestana) is { } pestanaDeUrl)
            _pestanaActiva = pestanaDeUrl;

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
        {
            _mensajeErrorFiltro = null;
            _mostrarGuardarFiltro = true;
        }
    }

    private readonly PaginationState _paginacion = new() { ItemsPerPage = 20 };

    // H2 (Project-Hydra-Negocio/tecnico/docs/ux-audit/02-clientes.md): paginador único en español, ver Clientes.razor.cs.
    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_totalElementos / (double)_paginacion.ItemsPerPage));

    private Task CambiarPaginaAsync(int pagina) => _paginacion.SetCurrentPageIndexAsync(pagina - 1);

    // H5 (Project-Hydra-Negocio/tecnico/docs/ux-audit/05-trabajadores-vehiculos.md): selector de tamaño de página, compartido por PaginadorSimple.razor.
    // Una sola petición: SetCurrentPageIndexAsync ya avisa a QuickGrid aunque la
    // página no cambie, así que refrescar además la rejilla pedía lo mismo dos
    // veces (ver RecargarAsync).
    private Task CambiarTamanoPaginaAsync(int tamano)
    {
        _paginacion.ItemsPerPage = tamano;
        return _paginacion.SetCurrentPageIndexAsync(0);
    }

    private QuickGrid<DocumentoListaDto>? _grid;
    private TemplateColumn<DocumentoListaDto>? _columnaEntidadAsociada;
    private TemplateColumn<DocumentoListaDto>? _columnaVigencia;
    private bool _ordenEntidadAsociadaPorAmbito;
    private bool _ordenVigenciaPorEmision;
    private ColumnBase<DocumentoListaDto>? _ultimaColumnaOrden;
    private bool _ultimoOrdenAscendente = true;
    private enum ColumnaOrdenListado { EntidadAsociada, Vigencia }
    private ColumnaOrdenListado? _columnaOrdenPendiente;
    private SortDirection _sentidoOrdenPendiente = SortDirection.Ascending;
    private static readonly GridSort<DocumentoListaDto> OrdenEntidadAsociadaListado = GridSort<DocumentoListaDto>.ByAscending(d => d.PropietarioNombre);
    private static readonly GridSort<DocumentoListaDto> OrdenAmbitoListado = GridSort<DocumentoListaDto>.ByAscending(d => d.Ambito);
    private static readonly GridSort<DocumentoListaDto> OrdenEmisionListado = GridSort<DocumentoListaDto>.ByAscending(d => d.FechaEmision);
    private static readonly GridSort<DocumentoListaDto> OrdenVencimientoListado = GridSort<DocumentoListaDto>.ByAscending(d => d.FechaVencimiento);

    private void CambiarCampoOrdenEntidadAsociada(bool porAmbito)
    {
        _ordenEntidadAsociadaPorAmbito = porAmbito;
        ProgramarOrdenListado(ColumnaOrdenListado.EntidadAsociada, _columnaEntidadAsociada);
    }

    private void CambiarCampoOrdenVigencia(bool porEmision)
    {
        _ordenVigenciaPorEmision = porEmision;
        ProgramarOrdenListado(ColumnaOrdenListado.Vigencia, _columnaVigencia);
    }

    private void ProgramarOrdenListado(ColumnaOrdenListado columna, ColumnBase<DocumentoListaDto>? referencia)
    {
        // Conserva el sentido elegido cuando cambia el campo de la columna que ya ordena.
        _sentidoOrdenPendiente = ReferenceEquals(_ultimaColumnaOrden, referencia) && !_ultimoOrdenAscendente
            ? SortDirection.Descending : SortDirection.Ascending;
        _columnaOrdenPendiente = columna;
    }

    private IReadOnlyList<OpcionEstado> OpcionesAmbitoListado =>
    [
        new(nameof(AmbitoAplicacion.Trabajador), Textos["ListaAmbitoTrabajador"].Value),
        new(nameof(AmbitoAplicacion.Cliente), Textos["ListaAmbitoClienteEmpresarial"].Value),
        new(nameof(AmbitoAplicacion.Empresa), Textos["ListaAmbitoEmpresa"].Value),
        new(nameof(AmbitoAplicacion.Vehiculo), Textos["ListaAmbitoVehiculo"].Value),
        new(nameof(AmbitoAplicacion.Proyecto), Textos["ListaAmbitoProyecto"].Value),
    ];

    /// <summary>
    /// La selección de estados que llega de fuera (la URL, un filtro guardado) reducida a nombres de
    /// <see cref="EstadoDocumento"/>; lo demás se descarta. Cadena vacía si no queda ninguno.
    /// </summary>
    private static string EstadosValidos(string? seleccion) =>
        SeleccionEstados.Unir(SeleccionEstados.Separar<EstadoDocumento>(seleccion).Select(e => e.ToString())) ?? string.Empty;

    private string EtiquetaAmbitoListado(AmbitoAplicacion ambito) =>
        OpcionesAmbitoListado.FirstOrDefault(o => o.Valor == ambito.ToString())?.Texto ?? ambito.ToString();

    private string _busqueda = string.Empty;
    private string _ambitoFiltro = string.Empty;
    private string _estadoFiltro = string.Empty;

    /// <summary>Documentos por estado para la franja, sin el filtro de estado aplicado. <c>null</c> hasta la primera carga.</summary>
    private IReadOnlyDictionary<string, int>? _recuentosPorEstado;

    /// <summary>Día de negocio con el que la columna de estado cuenta los días del motivo; se fija en cada carga.</summary>
    private DateOnly _hoy = DiaDeNegocio.Hoy();
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

    private async ValueTask<GridItemsProviderResult<DocumentoListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<DocumentoListaDto> request)
    {
        if (_servirPaginaEnMemoria)
        {
            // Refresco de una fila tras guardar en la vista rápida (ver RefrescarFilaAsync).
            _servirPaginaEnMemoria = false;
            return GridItemsProviderResult.From(_elementosPagina.ToList(), _totalElementos);
        }

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
            _ultimaColumnaOrden = request.SortByColumn;
            _ultimoOrdenAscendente = request.SortByAscending;
            var (ordenarPor, descendente) = LecturaOrden.Leer(request);
            (_ordenExportar, _descendenteExportar) = (ordenarPor, descendente);

            var ambitoFiltro = Enum.TryParse<AmbitoAplicacion>(_ambitoFiltro, out var ambito) ? ambito : (AmbitoAplicacion?)null;
            var estadosFiltro = SeleccionEstados.Separar<EstadoDocumento>(_estadoFiltro);

            var resultado = await Mediator.Send(new ObtenerDocumentosQuery(
                TrabajadorId: null,
                Ambito: ambitoFiltro,
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                Estado: null,
                Pagina: pagina,
                TamanoPagina: _paginacion.ItemsPerPage,
                OrdenarPor: ordenarPor,
                Descendente: descendente,
                Estados: estadosFiltro.Count == 0 ? null : estadosFiltro,
                ConRecuentosPorEstado: true), token);

            // La respuesta de un filtro ya abandonado no puede pisar el total,
            // las filas ni la selección de la pregunta que sí se está mirando.
            // QuickGrid descarta por su cuenta el resultado de un provider
            // superado, pero el estado de la página lo escribimos aquí.
            if (!EsVigente(carga))
                return GridItemsProviderResult.From(new List<DocumentoListaDto>(), 0);

            _totalElementos = resultado.TotalElementos;
            _totalConocido = resultado.TotalElementos;
            _recuentosPorEstado = resultado.RecuentosPorEstado;
            _hoy = DiaDeNegocio.Hoy();

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

    private async Task CambiarEstadoFiltroAsync(string? valor)
    {
        _estadoFiltro = valor ?? string.Empty;
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
                ToastService.MostrarError(resultado.Error);
                return;
            }

            var dto = resultado.Valor;
            var completo = dto.Eliminados == pedidos.Count && dto.Errores.Count == 0;

            // FS-09: el aviso ofrece «Deshacer» sobre los que sí cayeron. Sin él, la
            // única salida era pedir a un Administrador del Tenant que los recuperase
            // uno a uno desde Auditoría, pantalla que el resto de roles no abre.
            IReadOnlyList<Guid> eliminados = dto.IdsEliminados ?? [];

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
                completo ? TonoToast.Exito : dto.Eliminados == 0 ? TonoToast.Error : TonoToast.Advertencia,
                eliminados.Count > 0 ? Textos["ToastAccionDeshacer"].Value : null,
                eliminados.Count > 0 ? () => DeshacerEliminarLoteAsync(eliminados) : null);

            // Se retiran solo las fichas de los que cayeron (IdsEliminados); un
            // superviviente conserva la suya, con su edición sin guardar si la tenía.
            if (dto.Eliminados > 0)
                WorkspaceService.RetirarSiEstaAbierto(EntidadWorkspace.Documento, dto.IdsEliminados ?? pedidos);

            // El aviso se da siempre (la baja ocurrió y quien la pidió tiene que poder
            // deshacerla), pero la pantalla solo se toca si sigue siendo la misma: el
            // contexto se comprueba DESPUÉS del await, no solo en el finally. Aquí además se
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
    /// FS-09 (auditoría UX de flujos sin salida, 2026-09-24): «Deshacer» del aviso
    /// de una eliminación en lote. Restaura los que el lote sí eliminó, uno a uno
    /// con <see cref="RestaurarDocumentoCommand"/>.
    /// </summary>
    private bool _restaurandoLote;

    private async Task DeshacerEliminarLoteAsync(IReadOnlyList<Guid> ids)
    {
        if (_desechado || _restaurandoLote)
            return;

        _restaurandoLote = true;
        var token = _ciclo.Token;

        try
        {
            var r = await RestauracionEnLote.RestaurarAsync(ids, id => Mediator.Send(new RestaurarDocumentoCommand(id), token));

            ToastService.Mostrar(
                r.Errores.Count == 0
                    ? Textos["ToastLoteRestaurados", r.Restaurados].Value
                    : Textos["ToastLoteRestauradosConErrores", r.Restaurados, r.Errores.Count, string.Join(" ", r.Errores)].Value,
                r.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);

            if (r.Restaurados > 0)
                await RecargarAsync();
        }
        finally
        {
            _restaurandoLote = false;
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

    private string ObtenerClaseFila(DocumentoListaDto item)
    {
        var tinte = item.Estado switch
        {
            EstadoDocumento.Faltante or EstadoDocumento.Vencido => "fila-tintada-peligro",
            EstadoDocumento.Urgente => "fila-tintada-aviso",
            _ => null,
        };
        var foco = item.Id == _idEnfocado ? "fila-enfocada" : null;
        // «fila-pulsable»: un clic en la fila abre la vista rápida. QuickGrid no deja poner @onclick en
        // el <tr>: lo atiende el oyente delegado de atajos-lista.js, que pulsa «.nombre-abre-vista-rapida».
        return string.Join(' ', new[] { "fila-pulsable", foco, tinte }.Where(c => c is not null));
    }

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
                    // Enter abre la vista previa (el panel del documento), como el propietario de la fila.
                    await AbrirPanelAsync(idAbrir);
                }
                break;
        }

        StateHasChanged();
    }

    // --- Patrón único de lista (Project-Hydra-Negocio/tecnico/CONTRATO-PATRON-PANTALLA-LISTA-2026-09-28.md) ---

    /// <summary>
    /// Propietario de la fila, clic en la fila y Enter sobre la fila enfocada: la vista previa (pieza 6). En
    /// Documentos es el panel del Context Workspace, que ya existe; el contrato prohíbe sumarle un drawer.
    /// </summary>
    private Task AbrirPanelAsync(Guid id, string pestana = "informacion")
    {
        var documento = _elementosPagina.FirstOrDefault(e => e.Id == id);
        return documento is null
            ? Task.CompletedTask
            : WorkspaceService.AbrirAsync(EntidadWorkspace.Documento, documento.Id, documento.TipoDocumentoNombre, pestana);
    }

    /// <summary>Pestaña de la vista rápida a la que lleva la ventana de la celda «Plataformas».</summary>
    private const string PestanaValidacion = "validacion";

    /// <summary>
    /// Primera fila de la página (contando desde cero) en la que la ventana de «Plataformas» se abre hacia
    /// arriba. La tabla va en un envoltorio que desplaza en horizontal y que, por eso, recorta en vertical:
    /// una ventana que sobresale por debajo de la tabla le mete desplazamiento vertical propio, y una que
    /// sobresale por arriba queda recortada sin forma de alcanzarla. Con seis filas y la cabecera por
    /// encima caben los 280 px que el panel mide como mucho.
    /// </summary>
    private const int PrimeraFilaConVentanaHaciaArriba = 6;

    private VentanaContextoColocacion ColocacionVentanaPlataformas(Guid id) =>
        _elementosPagina.FindIndex(e => e.Id == id) >= PrimeraFilaConVentanaHaciaArriba
            ? VentanaContextoColocacion.ArribaCentro
            : VentanaContextoColocacion.AbajoCentro;

    private void AbrirGuardarFiltro()
    {
        _mensajeErrorFiltro = null;
        _mostrarGuardarFiltro = true;
    }

    private IReadOnlyList<OpcionEstado> OpcionesFiltrosGuardados =>
        _filtrosGuardados.Select(f => new OpcionEstado(f.Id.ToString(), f.Nombre)).ToList();

    private string EtiquetaFiltroBusqueda => Textos["ChipBusqueda", _busqueda].Value;

    private string EtiquetaFiltroAmbito =>
        Textos["ChipAmbito", _ambitoFiltro == nameof(AmbitoAplicacion.Cliente) ? Textos["ChipAmbitoCliente"].Value : _ambitoFiltro].Value;

    /// <summary>«N documentos»; con filtros, dice que el número es el de los que coinciden.</summary>
    private string TextoConteo
    {
        get
        {
            var uno = _totalElementos == 1;
            var clave = HayFiltrosActivos
                ? (uno ? "ConteoUnoFiltrado" : "ConteoVariosFiltrado")
                : (uno ? "ConteoUno" : "ConteoVarios");
            return Textos[clave, _totalElementos].Value;
        }
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
        _estadoFiltro = EstadosValidos(valores.Estado);

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

    /// <summary>
    /// P1-E2b: el modal «Guardar filtro» abre siempre con el nombre vacío, así que hay algo
    /// que perder en cuanto se ha escrito uno. Lo lee el ModalFormulario (guardián de la X, Escape, el fondo y «Cancelar», y aviso de
    /// navegación); cerrado (también tras guardar) nunca.
    /// </summary>
    private bool HayCambiosSinGuardar => _mostrarGuardarFiltro && !string.IsNullOrWhiteSpace(_nombreFiltroNuevo);

    private void CerrarModalGuardarFiltro(bool visible)
    {
        _mostrarGuardarFiltro = visible;
        if (!visible)
        {
            // Cancelar descarta el nombre: reabrir el modal sin tocarlo no es un cambio.
            _nombreFiltroNuevo = string.Empty;
            _mensajeErrorFiltro = null;
        }
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
        _mensajeErrorFiltro = null;

        try
        {
            var resultado = await Mediator.Send(
                new GuardarFiltroCommand(PantallasConFiltrosGuardados.Documentos, nombre, valoresJson), token);

            if (resultado.EsFallido)
            {
                // Como el resto del guardado: solo si el contexto sigue siendo el que lo pidió (otro contexto reinició el modal).
                if (ContextoSigueSiendo(contexto))
                    _mensajeErrorFiltro = resultado.Error.Mensaje;
                return;
            }

            var guardados = await Mediator.Send(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Documentos), token);
            ToastService.Mostrar("Filtro guardado.", TonoToast.Exito);

            // La pantalla solo se toca si sigue siendo la misma: el contexto se
            // comprueba DESPUÉS del await, no solo en el finally. El nombre a medio
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

    private void PedirEliminarFiltroGuardado(string idTexto) =>
        _filtroGuardadoAEliminar = _filtrosGuardados.FirstOrDefault(f => f.Id.ToString() == idTexto);

    private async Task ConfirmarEliminarFiltroGuardadoAsync()
    {
        if (_desechado || _eliminandoFiltroGuardado || _filtroGuardadoAEliminar is not { } filtro)
            return;

        var token = _ciclo.Token;
        _eliminandoFiltroGuardado = true;
        try
        {
            var resultado = await Mediator.Send(new EliminarFiltroGuardadoCommand(filtro.Id), token);
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            // Ya está borrado: se cierra el diálogo y se quita de la lista sin releerla, para que
            // un fallo de la relectura no deje la confirmación abierta sobre un filtro que no existe.
            _filtroGuardadoAEliminar = null;
            _filtrosGuardados = _filtrosGuardados.Where(f => f.Id != filtro.Id).ToList();
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            ToastService.Mostrar(Textos["FiltroGuardadoBorrarError"], TonoToast.Error);
        }
        finally
        {
            _eliminandoFiltroGuardado = false;
        }
    }

    /// <summary>El orden de la última carga de la rejilla, para que «Exportar esta vista» salga en el mismo.</summary>
    private string? _ordenExportar;
    private bool _descendenteExportar;

    /// <summary>
    /// Los criterios de «Exportar esta vista»: los mismos que esta página pasa a la consulta del
    /// listado, con los nombres de parámetro del endpoint de exportación, que los lee igual.
    /// </summary>
    private Dictionary<string, string?> CriteriosExportar => new()
    {
        ["q"] = _busqueda,
        ["ambito"] = _ambitoFiltro,
        ["estado"] = _estadoFiltro,
        ["orden"] = _ordenExportar,
        ["desc"] = _descendenteExportar ? "true" : null,
    };
}
