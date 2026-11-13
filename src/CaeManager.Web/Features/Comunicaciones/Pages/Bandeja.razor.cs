using CaeManager.Application.Comunicaciones.Commands.AplicarMacro;
using CaeManager.Domain.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Web.Features.Documentos.Recursos;
using CaeManager.Application.Common;
using CaeManager.Web.Features.Comunicaciones.Components;
using CaeManager.Web.Features.Comunicaciones;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Comunicaciones.Commands.ActualizarDocumentoDesdeAdjunto;
using CaeManager.Application.Comunicaciones.Commands.AsignarClienteConversacion;
using CaeManager.Application.Comunicaciones.Commands.AsignarEjecutivoConversacion;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Comunicaciones.Commands.CambiarEstadoConversacion;
using CaeManager.Application.Comunicaciones.Commands.ConfirmarClasificacionRuidoMensaje;
using CaeManager.Application.Comunicaciones.Commands.DescartarSugerenciaGestion;
using CaeManager.Application.Comunicaciones.Commands.DescartarSugerenciaVisita;
using CaeManager.Application.Comunicaciones.Commands.MigrarConversacionACorreo;
using CaeManager.Application.Comunicaciones.Commands.ResponderConversacion;
using CaeManager.Application.Comunicaciones.Commands.ResponderConversacionWhatsApp;
using CaeManager.Application.Comunicaciones.Commands.VincularConversacion;
using CaeManager.Application.Comunicaciones.Eventos;
using CaeManager.Application.Comunicaciones.Queries.DetectarActualizacionDocumentoDesdeAdjunto;
using CaeManager.Application.Gestiones.Commands.CrearGestionesParaTrabajador;
using CaeManager.Application.Comunicaciones.Queries.ObtenerCompanerosGestorCae;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Application.Comunicaciones.Queries.ObtenerFormatosRequeridosCentro;
using CaeManager.Application.Comunicaciones.Queries.ObtenerMacros;
using CaeManager.Application.Comunicaciones.Queries.ObtenerMensajesBuzonPersonal;
using CaeManager.Application.Comunicaciones.Queries.ObtenerNotasInternasConversacion;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Comunicaciones.Commands.PedirPrioridadValidacion;
using CaeManager.Application.Comunicaciones.Queries.ObtenerBorradorPedirPrioridad;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Services;
using CaeManager.Infrastructure.Autorizacion;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.Comunicaciones.Pages;

public record EjecutivoSelectorDto(Guid Id, string NombreCompleto);

/// <summary>
/// Communication Workspace unificado (Project-Hydra-Negocio/tecnico/docs/COMUNICACIONES.md § 16, paso 2
/// del rediseño): fusiona lo que antes eran Bandeja (correo) y Chat
/// (WhatsApp) — una sola bandeja, una sola conversación seleccionada, un
/// único ComposerBar que cambia de modo según Canal. El principio rector
/// (§ 10.2) es que el gestor nunca elige "voy a Correo" o "voy a WhatsApp":
/// entra a Comunicaciones y ve conversaciones.
/// </summary>
public partial class Bandeja : CaeManager.Web.Components.PaginaInteractiva, IAsyncDisposable
{
    [Inject] private DirectorioUsuariosTenant DirectorioUsuarios { get; set; } = default!;
    [Inject] private Microsoft.Extensions.Localization.IStringLocalizer<CaeManager.Web.Recursos.TextosComunes> TextosComunesPagina { get; set; } = default!;
    [Inject] private Microsoft.Extensions.Localization.IStringLocalizer<CaeManager.Web.Features.Comunicaciones.Recursos.TextosErroresConversaciones> ErroresTextos { get; set; } = default!;
    [Inject] private ILogger<Bandeja> Logger { get; set; } = default!;
    [Inject] private IOptions<ComunicacionesOptions> OpcionesComunicaciones { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private INotificadorMensajesTiempoReal Notificador { get; set; } = default!;
    [Inject] private ITenantActual TenantActual { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;
    [Inject] private TimeProvider Reloj { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "estado")] public string? EstadoInicial { get; set; }
    [SupplyParameterFromQuery(Name = "mes")] public string? MesInicial { get; set; }
    [SupplyParameterFromQuery(Name = "cliente")] public string? ClienteInicial { get; set; }
    [SupplyParameterFromQuery(Name = "q")] public string? BusquedaInicial { get; set; }

    /// <summary>
    /// Deep-link a una conversación concreta (Horizonte 2.6 de
    /// Project-Hydra-Negocio/MACRO_PLAN_2026-08-13.md, § 7 punto 3): a diferencia de "ctx" en el
    /// Context Workspace (ContextWorkspace.razor), aquí no hay un mecanismo
    /// genérico que restaurar — la Bandeja unificada es maestro-detalle
    /// propio, así que este parámetro y su sincronización viven en la propia
    /// página, con el mismo patrón "la URL manda" que el resto de filtros.
    /// </summary>
    [SupplyParameterFromQuery(Name = "conversacion")] public string? ConversacionInicial { get; set; }

    /// <summary>
    /// Toggle "Clientes | Mi buzón personal" (rediseño acordado, ver memoria
    /// hydra-buzon-personal-rediseno-pendiente): "clientes" (por defecto) o
    /// "personal" — la URL manda, mismo criterio que el resto de filtros de
    /// esta página.
    /// </summary>
    [SupplyParameterFromQuery(Name = "vista")] public string? VistaInicial { get; set; }

    /// <summary>
    /// Filtro Origen (lienzo «Documentos · listado sin pestañas», PR6b): «reclamaciones» muestra en la tabla el historial de lotes
    /// de reclamación enviados, que era la pestaña Reclamaciones de Documentos. La URL manda, como el resto de filtros; es el
    /// destino de «Ver reclamación» del hilo (<c>/comunicaciones?origen=reclamaciones</c>).
    /// </summary>
    [SupplyParameterFromQuery(Name = "origen")] public string? OrigenInicial { get; set; }

    [CascadingParameter] private Task<AuthenticationState>? EstadoAutenticacion { get; set; }

    private const string OrigenReclamaciones = "reclamaciones";
    private string _origen = string.Empty;

    /// <summary>
    /// El rol puede leer el historial de reclamaciones (<see cref="RolesComunicaciones.LecturaReclamaciones"/>, los mismos que lo ven
    /// hoy en Documentos). Consulta no: ni se le ofrece el Origen «Reclamaciones» ni se atiende desde la URL.
    /// </summary>
    private bool _puedeLeerReclamaciones;

    private bool OrigenReclamacionesActivo => _puedeLeerReclamaciones && _origen == OrigenReclamaciones;

    /// <summary>El historial ocupa el sitio de la tabla de conversaciones: solo en la vista de Clientes y sin conversación abierta.</summary>
    private bool MostrandoHistorial => OrigenReclamacionesActivo && _vista == "clientes" && _conversacionSeleccionadaId is null;

    private IReadOnlyList<OpcionEstado> OpcionesOrigen =>
        [new(OrigenReclamaciones, TextosReclamaciones["OrigenReclamaciones"].Value)];

    private async Task<bool> PuedeLeerReclamacionesAsync()
    {
        if (EstadoAutenticacion is null)
            return false;

        var usuario = (await EstadoAutenticacion).User;
        return RolesComunicaciones.LecturaReclamaciones
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(usuario.IsInRole);
    }

    /// <summary>
    /// Cambia el Origen. Pasar al historial cierra la conversación abierta (el historial ocupa la tabla) igual que cambiar de vista:
    /// con una respuesta a medias se pregunta antes. Vuelve a «Todos los orígenes» al elegir la opción que quita el filtro.
    /// </summary>
    private async Task CambiarOrigenAsync(string valor)
    {
        var nuevo = _puedeLeerReclamaciones && valor == OrigenReclamaciones ? OrigenReclamaciones : string.Empty;
        if (nuevo == _origen) return;

        if (!await _ambitoConversacion.ConfirmarAbandonoAsync()) return;

        _origen = nuevo;
        DescartarConversacionAbierta();
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["origen"] = nuevo.Length == 0 ? null : nuevo,
            ["conversacion"] = null
        });

        if (nuevo.Length == 0)
            await CargarListaAsync();
    }

    private string _vista = "clientes";
    private IReadOnlyList<MensajeBuzonPersonalDto> _mensajesBuzonPersonal = [];

    // --- Filtros ---
    private string _intencionFiltro = string.Empty;
    private static readonly IntencionCae[] IntencionesCae = Enum.GetValues<IntencionCae>();
    private string _estadoFiltro = string.Empty;
    private string _mesFiltro = string.Empty; // input type="month" -> "yyyy-MM"
    private string _clienteIdFiltro = string.Empty;
    private bool _soloAsignadasAMi;
    private bool _soloSinAsignar;
    private bool _soloEsperandoCliente;
    private bool _soloFueraDeSla;
    private bool _cambiandoPrioridad;
    private Task? _refrescoPlazos;

    private async Task RefrescarPlazosAsync()
    {
        using var reloj = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await reloj.WaitForNextTickAsync(_ciclo.Token))
                await InvokeAsync(async () =>
                {
                    if (_vista != "personal") await CargarListaAsync();
                    StateHasChanged();
                });
        }
        catch (OperationCanceledException) when (_ciclo.IsCancellationRequested) { }
    }

    private Task AlternarFueraDeSlaAsync()
    {
        _soloFueraDeSla = !_soloFueraDeSla;
        _pagina = 1;
        return CargarListaAsync();
    }

    private async Task CambiarPrioridadAsync(string valor)
    {
        if (_cambiandoPrioridad || _detalle is null || !Enum.TryParse<PrioridadConversacion>(valor, out var prioridad)) return;
        _cambiandoPrioridad = true;
        var vigencia = CapturarVigencia();
        try
        {
            var resultado = await Mediator.Send(new CaeManager.Application.Comunicaciones.Commands.CambiarPrioridadConversacion.CambiarPrioridadConversacionCommand(_detalle.Id, prioridad), _ciclo.Token);
            if (resultado.EsFallido) { ToastService.MostrarError(resultado.Error); return; }
            // Con otra conversación abierta, su detalle no cambió: solo la lista.
            if (MismaApertura(vigencia)) await CargarDetalleAsync();
            await CargarListaAsync();
        }
        finally { _cambiandoPrioridad = false; }
    }
    private string _busqueda = string.Empty;

    private bool _cargandoLista = true;
    private bool _errorCargaLista;
    private IReadOnlyList<ConversacionListaDto> _conversaciones = [];
    private Guid? _idEnfocado;

    // Paginación SQL compartida por la tabla inicial y la lista plana del hilo.
    // Ambas conservan el orden del resultado sin agrupar por Cliente empresarial.
    private const int TamanoPaginaBandeja = 20;
    private int _pagina = 1;
    private int _totalConversaciones;
    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_totalConversaciones / (double)TamanoPaginaBandeja));
    private IReadOnlyList<ClienteSelectorDto> _clientesSelector = [];
    private IReadOnlyList<EjecutivoSelectorDto> _ejecutivosDisponibles = [];
    private IReadOnlyList<CompaneroGestorCaeDto> _companeros = [];


    // --- Detalle / centro ---
    private Guid? _conversacionSeleccionadaId;
    private ConversacionDetalleDto? _detalle;
    private bool _cargandoDetalle;

    /// <summary>La última carga del detalle falló (maqueta 2026-11-01, E14): se pinta «No pudimos abrir la conversación» con «Reintentar» en vez de dejar el hilo anterior bajo la selección nueva.</summary>
    private bool _errorDetalle;

    /// <summary>Título del encabezado de la lista (E9): el filtro rápido activo, como hasta ahora.</summary>
    private string TituloLista =>
        TextosLista[_soloAsignadasAMi ? "Mias" : _soloSinAsignar ? "SinAsignar" : _soloEsperandoCliente ? "EsperandoCliente" : "Todas"].Value;
    private ClienteDetalleDto? _clienteActivo;
    private IReadOnlyList<MacroListaDto> _macrosDisponibles = [];
    private IReadOnlyList<CentroSelectorDto> _centrosClienteActivo = [];
    private IReadOnlyList<NotaInternaDetalleDto> _notasInternas = [];
    private IReadOnlyDictionary<Guid, string> _autoresNotas = new Dictionary<Guid, string>();

    /// <summary>
    /// Avatar elegido por cada autor de nota. Va por Id de usuario y no por conversación,
    /// así que se asigna al leerlo: una carga que llega tarde no pinta nada equivocado.
    /// </summary>
    private IReadOnlyDictionary<Guid, string> _avataresNotas = new Dictionary<Guid, string>();

    // --- Composer (compartido entre Correo/WhatsApp/fallback — ver ComposerBar) ---
    private string _textoRespuesta = string.Empty;
    private string _macroSeleccionadaId = string.Empty;
    private string _centroFormatosSeleccionado = string.Empty;
    private bool _enviando;
    private readonly List<AdjuntoParaEnviarDto> _adjuntosPendientes = [];
    private readonly AdjuntosCorreo _cargaAdjuntos = new();
    private string? _errorAdjuntos;
    private string _emailFallback = string.Empty;
    private string _ejecutivoSeleccionado = string.Empty;
    private bool _cambiandoEjecutivo;
    private bool _cambiandoEstado;

    private string _clienteTriageSeleccionado = string.Empty;
    private bool _asignandoCliente;

    // --- Actualizar documentación desde adjunto (§ 12.7) ---
    private bool _modalActualizarDocumentoVisible;
    private Guid _adjuntoParaActualizarDocumentoId;
    private bool _detectandoDocumento;
    private int _solicitudActualizarDocumentoVigente;
    private bool _aplicandoDocumento;
    private string? _errorActualizarDocumento;
    private IReadOnlyList<TipoDocumentoListaDto> _tiposDocumentoSelector = [];
    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresSelector = [];
    private IReadOnlyList<EmpresaSelectorDto> _empresasSelector = [];
    private string _tipoDocumentoIdFormulario = string.Empty;
    private string _propietarioDocumentoFormulario = nameof(AmbitoAplicacion.Trabajador);
    private string _trabajadorDocumentoIdFormulario = string.Empty;
    private string _empresaDocumentoIdFormulario = string.Empty;
    private string _fechaEmisionDocumentoFormulario = string.Empty;
    private string _fechaVencimientoDocumentoFormulario = string.Empty;

    /// <summary>La caducidad tiene un texto que no es una fecha entera (entrega «»): Aplicar no la envía como «sin caducidad».</summary>
    private bool _vencimientoDocumentoNoValido;

    /// <summary>La fecha de emisión del documento tiene un texto que no es una fecha entera: Actualizar queda bloqueado.</summary>
    private bool _emisionDocumentoNoValida;
    private bool _noCaducaDocumentoFormulario;
    private string _comentariosDocumentoFormulario = string.Empty;
    private int _confianzaDeteccionDocumento;

    private IDisposable? _suscripcionTiempoReal;

    /// <summary>
    /// Ciclo de vida de la pantalla: se cancela al retirarse el componente y
    /// su token viaja en TODAS las consultas y comandos de esta página. Sin
    /// él, una consulta lanzada justo antes de navegar seguía viva y volvía a
    /// escribir sobre un componente que ya no está en el árbol.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();

    /// <summary>
    /// Guarda de carga vigente para la lista (lista de clientes o buzón
    /// personal, que comparten <c>_cargandoLista</c> y su hueco de error).
    /// Se captura ANTES del await y se compara después: una respuesta de una
    /// carga que ya no es la vigente —cambiar de vista, cambiar de filtro,
    /// pasar de página— se descarta en vez de pintar la lista anterior sobre
    /// la actual. Comparar el filtro no bastaría: dos cargas del MISMO filtro
    /// (refresco en tiempo real, reintento) también pueden cruzarse.
    /// </summary>
    private int _cargaListaVigente;

    /// <summary>Número de la última carga cuyo resultado llegó a <c>_conversaciones</c> (no descartada por otra posterior).</summary>
    private int _cargaListaAplicada;

    /// <summary>Lo mismo para el detalle del hilo abierto — ver <see cref="CargarDetalleAsync"/>.</summary>
    private int _cargaDetalleVigente;

    /// <summary>Número de la última petición de «Compartir formatos» — ver <see cref="CompartirFormatosCentroAsync"/>.</summary>
    private int _solicitudFormatosVigente;

    /// <summary>Número de la última apertura del Drawer de «Pedir prioridad»; sube también al cerrarlo — ver <see cref="AbrirPedirPrioridadAsync"/>.</summary>
    private int _solicitudPrioridadVigente;

    // --- Vigencia de lo que espera (inventario de caminos asíncronos de la revisión Codex r6 de la #2167) ---
    //
    // Toda operación que escribe estado, borrador o selección después de una espera captura su Vigencia ANTES de esperar y la
    // comprueba DESPUÉS con MismaApertura o MismoContexto. Dos generaciones, subidas solo desde CambiarApertura y CambiarContexto:
    // - Apertura: sube al abrir otra conversación, al volver a la bandeja, al cambiar de vista y cuando la conversación abierta
    //   desaparece. Lo que pertenece al borrador o a la conversación abierta (adjuntos, lo enviado, la sucesora tras resolver, el
    //   destino de una vinculación, el medidor) solo se escribe con la misma apertura. Una recarga no la cambia: el borrador sigue
    //   siendo el mismo.
    // - Contexto: sube con cada apertura y además cuando una recarga cambia el Cliente empresarial de la conversación (o la deja sin
    //   contexto). Lo que depende de ese Cliente empresarial (plantilla, formatos de un Centro, Empresas y detección del adjunto,
    //   borrador de prioridad) solo se escribe con el mismo contexto.
    // Las operaciones que se pueden repetir o cancelar (otra elección de Centro, cerrar y reabrir un modal o un Drawer) llevan
    // además su propia solicitud. La carga del detalle conserva la suya (_cargaDetalleVigente), que sube con cada carga.

    private int _aperturaVigente;
    private int _contextoVigente;

    private readonly record struct Vigencia(int Apertura, int Contexto, Guid? ConversacionId);

    private Vigencia CapturarVigencia() => new(_aperturaVigente, _contextoVigente, _conversacionSeleccionadaId);

    private bool MismaApertura(Vigencia vigencia) => vigencia.Apertura == _aperturaVigente;

    private bool MismoContexto(Vigencia vigencia) => vigencia.Contexto == _contextoVigente;

    /// <summary>La conversación abierta deja de ser la misma: también cambia su contexto.</summary>
    private void CambiarApertura()
    {
        ++_aperturaVigente;
        CambiarContexto(conAviso: false);
    }

    /// <summary>
    /// El contexto de la conversación abierta ya no es el que era. El modal de «Actualizar documentación», cargado o cargándose,
    /// ofrece Empresas y lo detectado de ese contexto: se cierra (su solicitud deja de ser la vigente) y, si la conversación sigue
    /// abierta, se avisa para que se vuelva a abrir con los datos actuales (revisión Codex r6 de la #2167, MEDIO 4).
    /// Excepción, con la misma conversación abierta: si el Gestor CAE ya cambió algo en el modal (fechas, comentarios), no se
    /// cierra en silencio, porque perdería lo escrito. Queda abierto con sus valores, «Aplicar» bloqueado y el aviso de contexto
    /// cambiado; lo cierra él cuando haya copiado lo que necesite (revisión Codex r7 de la #2167, MEDIO 4). Cambiar de apertura
    /// (otra conversación, volver, otra vista) ya pasó por la confirmación de abandono, así que ahí se cierra como siempre.
    /// </summary>
    private void CambiarContexto(bool conAviso = true)
    {
        ++_contextoVigente;
        if (!_modalActualizarDocumentoVisible) return;

        if (conAviso && !_detectandoDocumento && HayCambiosActualizarDocumento)
        {
            _actualizarDocumentoContextoCaducado = true;
            return;
        }

        CerrarModalActualizarDocumento();
        if (conAviso) ToastService.Mostrar(ErroresTextos["AvisoActualizarDocumentoContextoCambiado"].Value, TonoToast.Info);
    }

    /// <summary>
    /// Recarga del detalle en curso (la que pinta el esqueleto, <c>_cargandoDetalle</c>). Se crea al empezar una carga y se completa
    /// cuando la carga vigente termina o se abandona (<see cref="TerminarRecargaDetalle"/>).
    /// </summary>
    private TaskCompletionSource? _recargaDetalle;

    private void TerminarRecargaDetalle()
    {
        var recarga = _recargaDetalle;
        _recargaDetalle = null;
        recarga?.TrySetResult();
    }

    /// <summary>
    /// Lo que depende del Cliente empresarial (una plantilla, el resumen de formatos de un Centro) y llega con una recarga del
    /// detalle en curso no se aplica todavía: esa recarga puede descubrir otro Cliente empresarial, y el contexto solo cambia
    /// cuando lo asigna (CambiarContexto). Se espera a que termine (y a las que empiecen mientras tanto) y se devuelve si el
    /// contexto sigue siendo el capturado (revisión Codex r7 de la #2167, MEDIO 1).
    /// </summary>
    private async Task<bool> ContextoConfirmadoAsync(Vigencia vigencia)
    {
        while (MismoContexto(vigencia) && _recargaDetalle is { } recarga)
            await recarga.Task;
        return MismoContexto(vigencia);
    }

    /// <summary>
    /// Callback de un componente hijo que, tras su propia espera, pide recargar el hilo (el recordatorio propuesto, ya enviado).
    /// La apertura viaja con el callback desde que se pintó: si el hijo era de otra apertura (se desmontó al abrir otra
    /// conversación), no se recarga la que hay ahora, que desmontaría su nota interna a medias (revisión Codex r7 de la #2167,
    /// MEDIO 3).
    /// </summary>
    private Task RecargarSiMismaAperturaAsync(int apertura) =>
        apertura == _aperturaVigente ? CargarDetalleAsync() : Task.CompletedTask;

    /// <summary>El callback lleva la apertura del momento en que se pinta, no la del momento en que el hijo lo invoca.</summary>
    private EventCallback RecargaDeEstaApertura()
    {
        var apertura = _aperturaVigente;
        return EventCallback.Factory.Create(this, () => RecargarSiMismaAperturaAsync(apertura));
    }

    private bool VentanaAbierta =>
        _detalle?.Canal == CanalConversacion.WhatsApp &&
        _detalle.FechaUltimoMensajeEntranteUtc is { } ultimo &&
        DateTime.UtcNow - ultimo < Conversacion.DuracionVentanaServicio;

    private string CierreVentanaLocal =>
        _detalle?.FechaUltimoMensajeEntranteUtc is { } ultimo
            ? ultimo.Add(Conversacion.DuracionVentanaServicio).EnHoraPeninsular().ToString("dd/MM 'a las' HH:mm")
            : string.Empty;

    // --- Action Center (Project-Hydra-Negocio/tecnico/docs/COMUNICACIONES.md § 12.6): agrega las sugerencias
    // de todos los mensajes de la conversación — antes cada una se pintaba
    // junto a su propio mensaje en UnifiedTimeline, ahora viven todas juntas
    // en AccionCenter, en la columna derecha. ---
    private IReadOnlyList<SugerenciaVisitaDetalleDto> SugerenciasVisitaPendientes =>
        _detalle?.Mensajes.Where(m => m.SugerenciaVisita is not null).Select(m => m.SugerenciaVisita!).ToList() ?? [];

    private IReadOnlyList<SugerenciaGestionDetalleDto> SugerenciasGestionPendientes =>
        _detalle?.Mensajes.SelectMany(m => m.SugerenciasGestion).ToList() ?? [];

    private bool HayAccionesPendientes =>
        SugerenciasVisitaPendientes.Count > 0 || SugerenciasGestionPendientes.Count > 0 || _detalle?.SugerenciaVinculacion is not null;

    protected override async Task OnInitializedAsync()
    {
        // Módulo congelado por defecto (ComunicacionesOptions, P2 #26 de
        // Project-Hydra-Negocio/MATURITY_REVIEW.md): sin ingesta real detrás, se
        // presenta como si la ruta no existiera en vez de mostrar una
        // bandeja que nadie va a alimentar de verdad.
        if (!OpcionesComunicaciones.Value.Activo)
        {
            NavigationManager.NavigateTo("/not-found");
            return;
        }

        // Antes de sincronizar la URL: el Origen «Reclamaciones» solo se atiende a quien lee el historial.
        _puedeLeerReclamaciones = await PuedeLeerReclamacionesAsync();

        // Los [Parameter] ya están asignados en este punto — se leen aquí y
        // no solo en OnParametersSet porque en el primer render
        // OnInitializedAsync corre ANTES que OnParametersSet, y la carga
        // inicial de abajo necesita los filtros ya resueltos (P1-18 de
        // Project-Hydra-Negocio/MATURITY_REVIEW.md).
        SincronizarFiltrosDesdeUrl();

        _clientesSelector = await Mediator.Send(new ObtenerClientesParaSelectorQuery(), _ciclo.Token);

        // Acotado al tenant activo: GetUsersInRoleAsync devuelve los gestores
        // de todas las organizaciones (AspNetUsers no tiene filtro global),
        // así que el selector listaba nombres de empleados de otros tenants.
        var gestores = await DirectorioUsuarios.ObtenerVisiblesEnRolAsync(Roles.GestorCae);
        _ejecutivosDisponibles = gestores
            .Select(u => new EjecutivoSelectorDto(u.Id, u.NombreCompleto))
            .ToList();

        await CargarCompanerosAsync();

        // La suscripción es del Tenant del circuito: los avisos de otros
        // Tenants nunca llegan aquí (el notificador ya segrega por Tenant), y si
        // la selección de Tenant beneficiario se retira en caliente se da de baja
        // del anterior y pasa al de origen sin esperar al cierre del circuito.
        // Refresca la bandeja unificada al instante cuando la ingesta de
        // WhatsApp persiste un mensaje nuevo — antes solo lo hacía /chat.
        // Si se salió de la pantalla durante las lecturas de arriba, DisposeAsync ya corrió: suscribirse ahora dejaría
        // una suscripción, y su oyente de la retirada, que nadie soltaría hasta el cierre del circuito.
        if (_ciclo.IsCancellationRequested)
            return;
        _suscripcionTiempoReal = SuscripcionMensajesDelTenantActual.Abrir(Notificador, TenantActual, AlRecibirMensajeAsync);

        if (_vista == "personal")
            await CargarBuzonPersonalAsync();
        else
            await CargarListaAsync();
        _refrescoPlazos = RefrescarPlazosAsync();
    }

    /// <summary>
    /// «Contactar con un compañero»: los demás Gestores CAE de mi Operador CAE. El acotado es de Application
    /// (<see cref="ObtenerCompanerosGestorCaeQuery"/>); aquí solo se pide. Es un dato auxiliar: si la lectura
    /// falla, la sección no aparece y la pantalla sigue funcionando, en vez de caerse por un listín.
    /// </summary>
    private async Task CargarCompanerosAsync()
    {
        try
        {
            _companeros = await Mediator.Send(new ObtenerCompanerosGestorCaeQuery(), _ciclo.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "No se pudieron leer los compañeros Gestores CAE para la pantalla de Comunicaciones.");
            _companeros = [];
        }
    }

    public async ValueTask DisposeAsync()
    {
        _suscripcionTiempoReal?.Dispose();

        // Antes de soltar nada más: cancelar lo que siga en vuelo. Cancelar y
        // liberar aquí es el equivalente asíncrono del IDisposable del patrón
        // — el componente ya implementa IAsyncDisposable, que es el que Blazor
        // llama, y añadir además IDisposable lo liberaría dos veces.
        await _ciclo.CancelAsync();
        if (_refrescoPlazos is not null) await _refrescoPlazos;
        _ciclo.Dispose();

        if (_moduloClipboard is not null)
        {
            // El circuito puede estar ya cerrado: soltar un módulo JS entonces
            // lanza, y una excepción aquí tumba la retirada del componente.
            try
            {
                await _moduloClipboard.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }

    /// <summary>Llega desde el hilo del job de fondo — todo lo que toque estado del componente va dentro de InvokeAsync.</summary>
    private Task AlRecibirMensajeAsync(MensajeWhatsAppRecibidoEvent aviso) =>
        InvokeAsync(async () =>
        {
            try
            {
                await CargarListaAsync();
                // Refresco ligero: NO pasa por SeleccionarConversacionAsync
                // para no perder lo que el gestor esté escribiendo a mitad
                // de una respuesta cuando llega un mensaje nuevo.
                if (aviso.ConversacionId == _conversacionSeleccionadaId)
                    await CargarDetalleAsync();
                StateHasChanged();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "No se pudo refrescar la bandeja al recibir un aviso de tiempo real.");
            }
        });

    /// <summary>
    /// Re-sincroniza los filtros con la URL en navegaciones posteriores
    /// dentro de la propia página (volver atrás, compartir la URL) — la
    /// recarga la sigue disparando explícitamente AplicarFiltrosAsync, no
    /// este método, para no depender del timing del router.
    /// </summary>
    protected override void OnParametersSet() => SincronizarFiltrosDesdeUrl();

    /// <summary>
    /// Igual que SincronizarFiltrosDesdeUrl pero async — OnParametersSet(Async)
    /// corre después de OnInitializedAsync incluso en el primer render (por
    /// eso basta con esto y no hace falta duplicar la llamada allí), así que
    /// cubre a la vez la carga en frío del deep-link y sus recargas
    /// posteriores (compartir la URL, atrás/adelante del navegador).
    ///
    /// La guarda contra <c>_conversacionSeleccionadaId</c> es lo que evita el
    /// bucle URL→selección→URL: SeleccionarConversacionAsync también escribe
    /// "conversacion" en la URL, lo que dispara este mismo método otra vez.
    /// </summary>
    protected override Task OnParametersSetAsync() =>
        Guid.TryParse(ConversacionInicial, out var idDeepLink) && idDeepLink != _conversacionSeleccionadaId
            ? SeleccionarConversacionAsync(idDeepLink)
            : Task.CompletedTask;

    private void SincronizarFiltrosDesdeUrl()
    {
        _estadoFiltro = EstadoInicial ?? string.Empty;
        _mesFiltro = MesInicial ?? string.Empty;
        _clienteIdFiltro = ClienteInicial ?? string.Empty;
        _busqueda = BusquedaInicial ?? string.Empty;

        // El Origen que llega por la URL (atrás/adelante, «Ver reclamación» del hilo): si cambia con una conversación abierta, la
        // conversación se abandona porque el historial ocupa su sitio. Un valor que no es «reclamaciones» es «todos los orígenes».
        // Quien no lee el historial (Consulta) tampoco lo fija desde la URL: su Origen queda en «todos» y un enlace como «Ver
        // reclamación» del hilo no le cierra la conversación abierta.
        var origenUrl = _puedeLeerReclamaciones && OrigenInicial == OrigenReclamaciones ? OrigenReclamaciones : string.Empty;
        if (origenUrl != _origen)
        {
            _origen = origenUrl;
            if (_conversacionSeleccionadaId is not null)
                DescartarConversacionAbierta();
        }

        var vistaUrl = VistaInicial == "personal" ? "personal" : "clientes";
        if (vistaUrl == _vista) return;

        // La vista cambió desde la URL (atrás/adelante, enlace compartido), no por el botón: lo que espera sobre la conversación
        // abierta en la otra vista deja de escribir igual que con CambiarVistaAsync (revisión Codex r6 de la #2167, BAJO).
        _vista = vistaUrl;
        DescartarConversacionAbierta();
    }

    /// <summary>
    /// La conversación abierta se abandona (volver a la bandeja, cambiar de vista por botón o por URL): la carga del detalle en
    /// vuelo deja de ser la vigente y la apertura y el contexto suben, de modo que nada de lo que esperaba la conversación
    /// abandonada escribe después (plantilla, formatos, adjuntos, modal de documentación, solicitudes de prioridad).
    /// </summary>
    private void DescartarConversacionAbierta()
    {
        ++_cargaDetalleVigente;
        _cargandoDetalle = false;
        _errorDetalle = false;
        _conversacionSeleccionadaId = null;
        _detalle = null;
        CambiarApertura();
        // La carga abandonada ya no llegará a su finally como vigente: lo que esperaba a que terminara se libera aquí (con el
        // contexto ya cambiado, así que descarta lo suyo).
        TerminarRecargaDetalle();
    }

    /// <summary>
    /// Cambia entre "Clientes" y "Mi buzón personal" — limpia la conversación
    /// seleccionada (una conversación de una vista no tiene sentido en la
    /// otra) y recarga la lista de la vista de destino. La URL manda, mismo
    /// criterio que el resto de filtros de esta página.
    /// </summary>
    private async Task CambiarVistaAsync(string vista)
    {
        if (_vista == vista) return;

        // Cambiar de vista desmonta el composer sin salir de la página: se pregunta
        // antes de tocar nada (P1-E2b).
        if (!await _ambitoConversacion.ConfirmarAbandonoAsync()) return;

        _vista = vista;
        // Igual que VolverABandejaAsync: la carga del detalle en vuelo deja de ser la vigente y todo lo que espera sobre la
        // conversación abandonada (plantilla, formatos, adjuntos, modal) deja de escribir (revisión Codex r6 de la #2167, MEDIO 1).
        DescartarConversacionAbierta();
        NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
        {
            ["vista"] = vista == "clientes" ? null : vista,
            ["conversacion"] = null
        });

        if (vista == "personal")
            await CargarBuzonPersonalAsync();
        else
            await CargarListaAsync();
    }

    private async Task CargarBuzonPersonalAsync()
    {
        var carga = ++_cargaListaVigente;
        _cargandoLista = true;
        _errorCargaLista = false;
        StateHasChanged();

        try
        {
            var mensajes = await Mediator.Send(new ObtenerMensajesBuzonPersonalQuery(), _ciclo.Token);
            if (carga != _cargaListaVigente) return;

            _mensajesBuzonPersonal = mensajes;
        }
        catch (OperationCanceledException)
        {
            // Retirada de la pantalla: no hay nada que contar al usuario.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al cargar el buzón personal dentro de Comunicaciones.");
            if (carga != _cargaListaVigente) return;

            _errorCargaLista = true;
        }
        finally
        {
            if (carga == _cargaListaVigente)
            {
                _cargandoLista = false;
                StateHasChanged();
            }
        }
    }

    private async Task CargarListaAsync()
    {
        // En «Mi buzón personal» la lista de la vista es el buzón: un aviso de tiempo real, un reintento o una acción sobre un
        // hilo abierto desde el buzón no puede cargar la lista de Clientes, que además dejaría de ser vigente la carga del buzón en
        // vuelo y apagaría su indicador con el buzón vacío (inventario de la revisión Codex r6 de la #2167).
        if (_vista == "personal")
        {
            await CargarBuzonPersonalAsync();
            return;
        }

        var carga = ++_cargaListaVigente;
        _cargandoLista = true;
        _errorCargaLista = false;
        StateHasChanged();

        try
        {
            // El input HTML type="month" entrega "yyyy-MM" — se parsea a mano
            // para no depender de que el formato coincida con la cultura actual.
            int? anio = null;
            int? mes = null;
            var partesMes = _mesFiltro.Split('-');
            if (partesMes.Length == 2 && int.TryParse(partesMes[0], out var anioParseado) && int.TryParse(partesMes[1], out var mesParseado))
            {
                anio = anioParseado;
                mes = mesParseado;
            }

            var resultado = await Mediator.Send(new ObtenerConversacionesQuery(
                Intencion: Enum.TryParse<IntencionCae>(_intencionFiltro, out var intencion) ? intencion : null,
                Estado: string.IsNullOrEmpty(_estadoFiltro) ? null : Enum.Parse<EstadoConversacion>(_estadoFiltro),
                Anio: anio,
                Mes: mes,
                ClienteId: Guid.TryParse(_clienteIdFiltro, out var clienteId) ? clienteId : null,
                SoloAsignadasAMi: _soloAsignadasAMi,
                SoloSinAsignar: _soloSinAsignar,
                SoloEsperandoCliente: _soloEsperandoCliente,
                SoloFueraDeSla: _soloFueraDeSla,
                Busqueda: string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                Pagina: _pagina,
                TamanoPagina: TamanoPaginaBandeja), _ciclo.Token);

            // Guarda de carga vigente: si mientras esta consulta viajaba se
            // pidió otra —otro filtro, otra página, la otra vista—, lo que
            // vuelve aquí ya no es la lista que el gestor está mirando.
            if (carga != _cargaListaVigente) return;

            // Sin filtro de Canal a propósito (Project-Hydra-Negocio/tecnico/docs/COMUNICACIONES.md § 10.2):
            // el gestor ve conversaciones, no "correo" o "WhatsApp" por separado.
            _conversaciones = resultado.Elementos;
            _totalConversaciones = resultado.TotalElementos;
            _cargaListaAplicada = carga;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // _errorCargaLista solo pinta un aviso genérico: sin este log, un
            // fallo al cargar la bandeja no deja ningún rastro que permita
            // diagnosticarlo después.
            Logger.LogError(ex, "Error al cargar la lista de conversaciones de la bandeja.");
            if (carga != _cargaListaVigente) return;

            _errorCargaLista = true;
        }
        finally
        {
            if (carga == _cargaListaVigente)
            {
                _cargandoLista = false;
                StateHasChanged();
            }
        }
    }

    /// <summary>
    /// Los ocho filtros de la bandeja: cuatro que viajan en la URL (estado,
    /// mes, Cliente empresarial y búsqueda), intención CAE y tres conmutadores de asignación.
    /// </summary>
    private bool HayFiltrosActivos =>
        !string.IsNullOrEmpty(_intencionFiltro) ||
        !string.IsNullOrWhiteSpace(_estadoFiltro) || !string.IsNullOrWhiteSpace(_mesFiltro)
        || !string.IsNullOrWhiteSpace(_clienteIdFiltro) || !string.IsNullOrWhiteSpace(_busqueda)
        || _soloAsignadasAMi || _soloSinAsignar || _soloEsperandoCliente || _soloFueraDeSla;

    /// <summary>
    /// Los ocho de una vez y una sola recarga, por el mismo motivo que
    /// documenta <see cref="AplicarFiltrosAsync"/>: encadenar navegaciones
    /// arriesga que cada una lea la URL sin el cambio de la anterior.
    /// </summary>
    private Task LimpiarFiltrosAsync()
    {
        _intencionFiltro = string.Empty;
        _estadoFiltro = string.Empty;
        _mesFiltro = string.Empty;
        _clienteIdFiltro = string.Empty;
        _busqueda = string.Empty;
        _soloAsignadasAMi = false;
        _soloSinAsignar = false;
        _soloEsperandoCliente = false;
        _soloFueraDeSla = false;
        return AplicarFiltrosAsync();
    }

    private Task AplicarFiltrosAsync()
    {
        // Los cuatro a la vez en una sola navegación — llamar a
        // ActualizarFiltroEnUrl varias veces seguidas arriesgaría que cada
        // NavigateTo lea la URL todavía sin el cambio del anterior.
        // Filtrar no sale de la página ni borra la respuesta a medias: esa
        // navegación no pregunta (ver HayCambiosSinGuardar).
        _navegandoDentroDeLaPagina = true;
        try
        {
            NavigationManager.ActualizarFiltrosEnUrl(new Dictionary<string, string?>
            {
                ["estado"] = _estadoFiltro,
                ["mes"] = _mesFiltro,
                ["cliente"] = _clienteIdFiltro,
                ["q"] = _busqueda
            });
        }
        finally
        {
            _navegandoDentroDeLaPagina = false;
        }

        // Cambiar cualquier filtro vuelve a la página 1 — quedarse en la 3 de
        // un filtro que ahora tiene una sola página dejaría la lista en blanco.
        _pagina = 1;
        return CargarListaAsync();
    }

    private Task FiltrarAsignadasAMiAsync()
    {
        _soloAsignadasAMi = !_soloAsignadasAMi;
        if (_soloAsignadasAMi) _soloSinAsignar = false;
        _pagina = 1;
        return CargarListaAsync();
    }

    private Task FiltrarSinAsignarAsync()
    {
        _soloSinAsignar = !_soloSinAsignar;
        if (_soloSinAsignar) _soloAsignadasAMi = false;
        _pagina = 1;
        return CargarListaAsync();
    }

    private Task VerTodasAsync()
    {
        _soloAsignadasAMi = false;
        _soloSinAsignar = false;
        _pagina = 1;
        return CargarListaAsync();
    }

    /// <summary>
    /// "Esperando cliente" sigue siendo un estado derivado, no persistido
    /// (§ 16.4) — pero el filtro ya no se aplica en memoria sobre la lista
    /// cargada: con paginación en SQL, filtrar aquí dejaría páginas que
    /// parecen vacías cuando en realidad hay coincidencias más adelante. Va
    /// al servidor con la misma mecánica que SoloAsignadasAMi/SoloSinAsignar.
    /// </summary>
    private Task AlternarEsperandoCliente()
    {
        _soloEsperandoCliente = !_soloEsperandoCliente;
        _pagina = 1;
        return CargarListaAsync();
    }

    private Task CambiarPaginaBandejaAsync(int pagina)
    {
        _pagina = pagina;
        return CargarListaAsync();
    }

    /// <summary>El teclado recorre el mismo orden que la tabla y la lista plana del hilo.</summary>
    private List<ConversacionListaDto> ConversacionesVisiblesEnOrden() => _conversaciones.ToList();

    private bool HaySiguienteConversacion
    {
        get
        {
            var indice = ConversacionesVisiblesEnOrden().FindIndex(c => c.Id == _conversacionSeleccionadaId);
            return !_errorCargaLista && indice >= 0 && (indice + 1 < _conversaciones.Count || _pagina < TotalPaginas);
        }
    }

    private async Task SiguienteConversacionAsync()
    {
        if (_cargandoLista || _errorCargaLista) return;
        var filas = ConversacionesVisiblesEnOrden();
        var indice = filas.FindIndex(c => c.Id == _conversacionSeleccionadaId);
        if (indice < 0) return;
        if (indice + 1 < filas.Count)
        {
            await SeleccionarConversacionAsync(filas[indice + 1].Id);
            return;
        }
        if (_pagina < TotalPaginas)
        {
            if (!await _ambitoConversacion.ConfirmarAbandonoAsync()) return;
            var vigencia = CapturarVigencia();
            _pagina++;
            // Un refresco de fondo no invalida esta carga: lo que cuenta es que la lista aplicada
            // sea posterior a la petición y que el Gestor CAE no haya navegado a otra conversación
            // (ni reabierto la misma: comparar el id dejaba pasar A→B→A).
            var cargada = await RecargarListaFrescaAsync();
            if (cargada && MismaApertura(vigencia) && _conversaciones.FirstOrDefault() is { } siguiente)
                await SeleccionarConversacionAsync(siguiente.Id);
        }
    }

    /// <summary>
    /// Recarga la lista y confirma que lo que quedó en <c>_conversaciones</c> se pidió DESPUÉS de esta llamada.
    /// Si un refresco de fondo (aviso de tiempo real) se adelantó y descartó esta carga, el resultado vigente
    /// aún puede estar en vuelo; se repite una vez, que es más barato que decidir sobre una lista vieja.
    /// </summary>
    private async Task<bool> RecargarListaFrescaAsync()
    {
        for (var intento = 0; intento < 2; intento++)
        {
            var propia = _cargaListaVigente + 1;
            await CargarListaAsync();
            if (!_errorCargaLista && _cargaListaAplicada >= propia) return true;
        }

        return false;
    }

    /// <summary>
    /// j/k mueven el foco por la lista y Enter abre la conversación enfocada.
    /// <c>atajos-lista.js</c> ya descarta las teclas con el foco en un textarea, input o select
    /// (el Composer) y con Ctrl/Meta/Alt, así que escribir una respuesta nunca llega aquí.
    /// </summary>
    private async Task ManejarAtajoListaAsync(string tecla)
    {
        // Con el historial de reclamaciones en pantalla la lista de conversaciones no se ve: j/k/Enter no abren nada a ciegas.
        if (MostrandoHistorial) return;

        var filas = ConversacionesVisiblesEnOrden();
        if (filas.Count == 0) return;

        var indice = _idEnfocado is null ? -1 : filas.FindIndex(f => f.Id == _idEnfocado);
        switch (tecla)
        {
            case "j":
                _idEnfocado = filas[Math.Min(indice + 1, filas.Count - 1)].Id;
                break;
            case "k":
                _idEnfocado = filas[Math.Max(indice - 1, 0)].Id;
                break;
            case "Enter" when indice >= 0:
                await SeleccionarConversacionAsync(filas[indice].Id);
                return;
        }

        StateHasChanged();
    }

    private bool _notaInternaActiva;

    private async Task VolverABandejaAsync()
    {
        if (!await _ambitoConversacion.ConfirmarAbandonoAsync()) return;
        DescartarConversacionAbierta();
        _clienteActivo = null;
        _notaInternaActiva = false;
        _textoRespuesta = string.Empty;
        _adjuntosPendientes.Clear();
        _errorAdjuntos = null;
        NavigationManager.ActualizarFiltroEnUrl("conversacion", null);
    }

    private async Task SeleccionarConversacionAsync(Guid id)
    {
        // Otra conversación desmonta el composer (y su nota, por su @key) sin salir de la
        // página: con una respuesta o nota a medias se pregunta ANTES de cambiar la
        // selección, la URL o el detalle; «Seguir editando» deja todo como estaba (P1-E2b).
        // Pulsar la conversación que ya está abierta no cambia nada: ni pregunta ni recarga
        // (confirmar un «descarte» sin navegación después dejaría pasar sin preguntar la
        // siguiente salida; revisión Codex del lote C).
        if (id == _conversacionSeleccionadaId) return;

        if (!await _ambitoConversacion.ConfirmarAbandonoAsync()) return;

        _conversacionSeleccionadaId = id;
        CambiarApertura();
        _idEnfocado = id;
        _notaInternaActiva = false;
        _textoRespuesta = string.Empty;
        _macroSeleccionadaId = string.Empty;
        _clienteTriageSeleccionado = string.Empty;
        _adjuntosPendientes.Clear();
        _errorAdjuntos = null;
        _centroFormatosSeleccionado = string.Empty;
        _emailFallback = string.Empty;

        // Deep-link (Horizonte 2.6 de Project-Hydra-Negocio/MACRO_PLAN_2026-08-13.md): la URL queda
        // como la fuente de verdad de qué conversación está abierta, mismo
        // patrón que el resto de filtros de esta página (y que "ctx" en el
        // Context Workspace). replace: seleccionar un hilo no es un paso de
        // navegación propio para "Atrás" del navegador.
        //
        // Solo si de verdad cambia algo: ActualizarFiltroEnUrl navega
        // incondicionalmente, y OnParametersSetAsync llega hasta aquí
        // precisamente cuando "conversacion" YA trae este id (carga en frío
        // de un deep-link). NavigateTo durante el prerenderizado estático de
        // InteractiveServer (antes de que exista circuito) se resuelve como
        // una redirección HTTP real — repetirla a la misma URL en cada vuelta
        // del prerender es el bucle de "demasiadas redirecciones" que se veía
        // al abrir un enlace de conversación en una pestaña nueva.
        if (id.ToString() != ConversacionInicial)
            NavigationManager.ActualizarFiltroEnUrl("conversacion", id.ToString());

        await CargarDetalleAsync();
    }

    private async Task<(IReadOnlyList<NotaInternaDetalleDto> Notas, IReadOnlyDictionary<Guid, string> Autores, IReadOnlyDictionary<Guid, string> Avatares)> CargarNotasInternasAsync(Guid conversacionId)
    {
        var notas = await Mediator.Send(new ObtenerNotasInternasConversacionQuery(conversacionId), _ciclo.Token);
        if (notas.Count == 0) return (notas, new Dictionary<Guid, string>(), new Dictionary<Guid, string>());

        var idsAutores = notas.Select(n => n.AutorUsuarioId).Distinct().ToList();
        var autores = await DirectorioUsuarios.ObtenerNombresVisiblesAsync(idsAutores, _ciclo.Token);
        var avatares = await DirectorioUsuarios.ObtenerAvataresVisiblesAsync(idsAutores, _ciclo.Token);
        return (notas, autores, avatares);
    }

    /// <summary>Tras guardar una nota: solo se recargan las notas, no el hilo entero ni sus selectores.</summary>
    private async Task RecargarNotasInternasAsync()
    {
        if (_conversacionSeleccionadaId is not { } id) return;

        var carga = _cargaDetalleVigente;
        var (notas, autores, avatares) = await CargarNotasInternasAsync(id);
        if (carga != _cargaDetalleVigente) return;

        _notasInternas = notas;
        _autoresNotas = autores;
        _avataresNotas = avatares;
    }

    /// <summary>
    /// Conversación y Cliente empresarial a los que pertenece el contexto en memoria (ficha, macros, centros y selectores).
    /// <c>_detalle</c> se asigna a mitad de la carga, antes que ese contexto: su Id no dice de quién es el contexto, y una carga
    /// solapada puede dejarlo a medias (revisión puente del delta r1 de la #2167, tres pasadas). Se invalida al asignar un
    /// detalle que no es el de ese par y se fija al terminar la carga con éxito; si una carga falla, solo se conserva el detalle
    /// con el contexto cuando el par coincide.
    /// </summary>
    private (Guid ConversacionId, Guid? Cliente)? _contextoDe;

    private bool ContextoEs(Guid conversacionId, Guid? cliente) =>
        _contextoDe is { } c && c.ConversacionId == conversacionId && c.Cliente == cliente;

    // Un único acceso a ese campo del detalle para el contexto: deuda terminológica heredada (Empresa.Id en posición de Cliente empresarial).
    private static Guid? ClienteDelDetalle(ConversacionDetalleDto detalle) => detalle.ClienteId;

    private async Task CargarDetalleAsync()
    {
        if (_conversacionSeleccionadaId is not { } id) return;

        var carga = ++_cargaDetalleVigente;
        _cargandoDetalle = true;
        _recargaDetalle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _errorDetalle = false;
        StateHasChanged();

        try
        {
            var detalle = await Mediator.Send(new ObtenerConversacionPorIdQuery(id), _ciclo.Token);

            // Guarda de carga vigente: el detalle de un hilo que el gestor ya
            // no tiene abierto no puede sobrescribir el que sí. Sin esto, abrir
            // dos hilos seguidos dejaba en pantalla el que respondiera último,
            // que no es necesariamente el elegido.
            if (carga != _cargaDetalleVigente) return;

            // Las notas internas viajan aparte del detalle (su audiencia es más
            // estrecha: ver ObtenerNotasInternasConversacionQuery) y se asignan
            // junto a él, bajo la misma guarda de carga vigente, para que el
            // timeline de un hilo nunca muestre las notas de otro.
            IReadOnlyList<NotaInternaDetalleDto> notas = [];
            IReadOnlyDictionary<Guid, string> autores = new Dictionary<Guid, string>();
            IReadOnlyDictionary<Guid, string> avatares = new Dictionary<Guid, string>();
            if (detalle is not null)
                (notas, autores, avatares) = await CargarNotasInternasAsync(id);
            if (carga != _cargaDetalleVigente) return;

            _detalle = detalle;
            // Otra conversación o el mismo hilo con otro Cliente empresarial: el contexto en memoria deja de ser el suyo y lo que
            // espera sobre el anterior deja de escribir (CambiarContexto, también cierra el modal de documentación cargado).
            if (detalle is not null && !ContextoEs(id, ClienteDelDetalle(detalle)))
            {
                _contextoDe = null;
                CambiarContexto();
            }
            _notasInternas = notas;
            _autoresNotas = autores;
            _avataresNotas = avatares;

            // Enlace corrupto o conversación fuera de alcance (borrada, de
            // otro tenant, sin visibilidad): mismo criterio que
            // ContextWorkspace.TryParsearCtx con un "ctx" inválido — se
            // ignora en silencio y la página vuelve al estado "sin selección"
            // en vez de quedarse con un panel central en blanco para siempre.
            if (_detalle is null)
            {
                _conversacionSeleccionadaId = null;
                CambiarApertura();
                NavigationManager.ActualizarFiltroEnUrl("conversacion", null);
                return;
            }

            _ejecutivoSeleccionado = _detalle.EjecutivoAsignadoId?.ToString() ?? string.Empty;

            if (_detalle.ClienteId is not null)
            {
                var clienteActivo = await Mediator.Send(new ObtenerClientePorIdQuery(_detalle.ClienteId.Value), _ciclo.Token);
                var macros = await Mediator.Send(new ObtenerMacrosQuery(_detalle.ClienteId), _ciclo.Token);
                var centros = await Mediator.Send(new ObtenerCentrosParaSelectorQuery(ClienteId: _detalle.ClienteId), _ciclo.Token);
                if (carga != _cargaDetalleVigente) return;

                _clienteActivo = clienteActivo;
                _macrosDisponibles = macros;
                _centrosClienteActivo = centros;
                RetirarCentroNoDisponible(centros);
            }
            else
            {
                // Las macros se asignan después de comprobar la carga vigente, como en la rama con Cliente empresarial: una lectura
                // tardía de una carga antigua no sustituye las macros de la conversación abierta (revisión Codex r3 de la #2167, MEDIO 2).
                var macros = await Mediator.Send(new ObtenerMacrosQuery(), _ciclo.Token);
                if (carga != _cargaDetalleVigente) return;

                _clienteActivo = null;
                _macrosDisponibles = macros;
                _centrosClienteActivo = [];
                RetirarCentroNoDisponible([]);
            }

            // Selectores del Action Center (Project-Hydra-Negocio/tecnico/docs/COMUNICACIONES.md § 12.6): se
            // cargan aquí en vez de solo al abrir el modal de "Actualizar
            // documento" (más abajo) porque la revisión de una sugerencia de
            // gestión puede necesitarlos antes de que el gestor toque ese
            // otro flujo. Sin filtro de Cliente, pero sí de cartera (el mismo que
            // usa ese modal): CrearGestionesParaTrabajador rechaza un
            // Trabajador fuera de la Asignación de Cartera del Gestor CAE.
            var tiposDocumento = await Mediator.Send(new ObtenerTiposDocumentoQuery(ParaAltaNueva: true), _ciclo.Token);
            var trabajadores = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery(AlcanceSelectorTrabajadores.Cartera), _ciclo.Token);
            if (carga != _cargaDetalleVigente) return;

            _tiposDocumentoSelector = tiposDocumento;
            _trabajadoresSelector = trabajadores;
            _contextoDe = (id, ClienteDelDetalle(_detalle));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al abrir la conversación {ConversacionId}.", id);
            if (carga != _cargaDetalleVigente) return;

            _errorDetalle = true;
            // Sin hilo ni contexto de la conversación anterior: la columna de acciones no puede operar sobre otra (revisión puente).
            // Si el detalle en memoria ES el de esta conversación (una recarga: un mensaje nuevo, un envío), se conserva con su
            // contexto: vaciarlo desmontaría el panel y destruiría el borrador de la nota fija sin pasar por la confirmación de
            // abandono (revisión Codex r1 de la #2167, MEDIO 2). El error se avisa igualmente con el toast.
            // Una recarga que falla conserva el detalle y su contexto solo si el contexto en memoria es el de ESTA conversación y
            // este Cliente empresarial (_contextoDe); si no, el contexto sería de otro hilo (revisión puente del delta r1 de la #2167).
            if (!(_detalle is { } actual && ContextoEs(id, ClienteDelDetalle(actual))))
            {
                _contextoDe = null;
                CambiarContexto();
                _clienteActivo = null;

                // Excepción: con la nota fija de esta conversación en edición el detalle se conserva sin contexto (el panel solo
                // pinta esa nota, PanelContextoVisible); vaciarlo desmontaría el borrador sin confirmar el abandono (revisión
                // Codex r3 de la #2167, MEDIO 1: una segunda recarga que falla tras otra que cambió el Cliente empresarial).
                if (!NotaFijaEnEdicionDeLaConversacion(id)) _detalle = null;
            }
            ToastService.Mostrar(ErroresTextos["ErrorAbrirConversacion"].Value, TonoToast.Error);
        }
        finally
        {
            if (carga == _cargaDetalleVigente)
            {
                _cargandoDetalle = false;
                TerminarRecargaDetalle();
                StateHasChanged();
            }
        }
    }

    /// <summary>
    /// Una recarga que cambia el Cliente empresarial sustituye los Centros disponibles: si el elegido en «Compartir formatos de
    /// centro» ya no está entre ellos, se retira la selección y «Pedir prioridad» deja de ofrecerse sobre él (revisión Codex r5
    /// de la #2167, MEDIO 3).
    /// </summary>
    private void RetirarCentroNoDisponible(IReadOnlyList<CentroSelectorDto> disponibles)
    {
        if (!Guid.TryParse(_centroFormatosSeleccionado, out var elegido) || disponibles.All(c => c.Id != elegido))
            _centroFormatosSeleccionado = string.Empty;
    }

    private bool _aplicandoMacro;

    // ---- Respuesta sugerida por Vega (E18) ----

    private RespuestaSugeridaPanel? _panelSugerencia;

    /// <summary>Mensaje que contestaría una sugerencia: el último entrante del hilo abierto.</summary>
    private Guid? UltimoMensajeEntranteId => _detalle?.Mensajes
        .Where(m => m.Direccion == DireccionMensaje.Entrante)
        .OrderByDescending(m => m.FechaUtc)
        .Select(m => (Guid?)m.Id)
        .FirstOrDefault();

    /// <summary>
    /// Se ofrece pedir una sugerencia si la conversación sigue viva y su último mensaje es del contacto. Es la misma regla que
    /// aplica GenerarRespuestaSugeridaCommand (que además ignora los mensajes automáticos, que este DTO no distingue): aquí
    /// solo decide si se muestra el botón; el servidor decide en firme.
    /// </summary>
    private bool PuedeSugerirRespuesta =>
        _detalle is { Estado: not (EstadoConversacion.Resuelta or EstadoConversacion.Cerrada) } detalle
        && detalle.Mensajes.OrderByDescending(m => m.FechaUtc).FirstOrDefault()?.Direccion == DireccionMensaje.Entrante;

    /// <summary>«Insertar para revisar»: el texto va al redactor y no se envía nada. Si ya había algo, el panel ha preguntado antes.</summary>
    private Task InsertarSugerenciaAsync(RespuestaSugeridaPanel.SugerenciaDeLaSeleccion aviso)
    {
        // Una devolución tardía de otra conversación no escribe en el redactor de la que está abierta. La apertura cambia
        // al pulsar otra conversación y el detalle solo cuando termina de cargar (revisión Codex r1, ALTO 2): hacen falta las
        // dos. Y la de una apertura anterior de esta misma conversación tampoco (A→B→A, revisión Codex r2, MEDIO 1): el panel
        // recibe la apertura vigente como token de selección (_aperturaVigente, que una recarga no cambia).
        var sugerencia = aviso.Sugerencia;
        if (aviso.Seleccion != _aperturaVigente || !EsLaConversacionAbierta(sugerencia.ConversacionId)) return Task.CompletedTask;

        // La persona aceptó sustituir el redactor que había al pulsar. Si mientras el servidor respondía siguió escribiendo,
        // eso es suyo y no se pisa (revisión Codex r3, MEDIO): la sugerencia queda sin revisar fuera del redactor, y el panel
        // ofrece ponerla preguntando antes. Un redactor vaciado entretanto no tiene nada que perder.
        if (!string.IsNullOrWhiteSpace(_textoRespuesta) && !string.Equals(_textoRespuesta, aviso.TextoAlPedir, StringComparison.Ordinal))
            return Task.CompletedTask;

        // En correo la sugerencia se guarda como HTML; el redactor es texto (revisión Codex r1, MEDIO 8). El servidor compara
        // los dos como texto (RevisionRespuestaSugerida.TextoComparable), así que el bloqueo es el mismo.
        _textoRespuesta = RespuestaSugeridaPanel.TextoParaRedactor(sugerencia, _detalle!.Canal == CanalConversacion.WhatsApp);
        _notaInternaActiva = false;
        _sugerenciaEnRedactor = new SugerenciaEnRedactor(sugerencia.Id, _aperturaVigente);
        return Task.CompletedTask;
    }

    /// <summary>
    /// La sugerencia que esta página puso en su redactor y en qué apertura (revisión Codex r3 de la #2163, ALTO). Solo esa se
    /// ha mostrado aquí, y solo esa pueden confirmar «Lo he revisado» o el envío de un texto editado más del umbral. Abrir otra
    /// conversación, o volver a esta, cambia la apertura y la deja sin efecto.
    /// </summary>
    private SugerenciaEnRedactor? _sugerenciaEnRedactor;

    private readonly record struct SugerenciaEnRedactor(Guid Id, int Apertura);

    /// <summary>
    /// Lo que la persona escribe en el redactor. Si lo vacía, lo que escriba después ya no sale de la sugerencia insertada:
    /// deja de contar como en el redactor y no la confirma (revisión puente de la r3 de la #2163). Sustituir el texto sin
    /// vaciarlo antes (seleccionar y pegar) no se distingue de editarlo; quien lo hace sí ha visto la sugerencia aquí.
    /// </summary>
    private void CambiarTextoRespuesta(string texto)
    {
        _textoRespuesta = texto;
        if (string.IsNullOrWhiteSpace(texto)) _sugerenciaEnRedactor = null;
    }

    private Guid? SugerenciaEnRedactorVigente =>
        _sugerenciaEnRedactor is { } enRedactor && enRedactor.Apertura == _aperturaVigente ? enRedactor.Id : null;

    /// <summary>La conversación seleccionada y la que muestra el detalle son esta: ni la anterior, ni una que aún carga.</summary>
    private bool EsLaConversacionAbierta(Guid conversacionId) =>
        _conversacionSeleccionadaId == conversacionId && _detalle?.Id == conversacionId;

    /// <summary>
    /// Tras descartar, el redactor no se queda con el texto descartado: el servidor sigue bloqueando enviarlo y la persona
    /// acabaría ante un rechazo de algo que acaba de descartar. Si ya lo editó más del umbral, es suyo y se respeta.
    /// </summary>
    private Task SugerenciaDescartadaAsync(RespuestaSugeridaPanel.SugerenciaDeLaSeleccion aviso)
    {
        var sugerencia = aviso.Sugerencia;
        if (aviso.Seleccion == _aperturaVigente && EsLaConversacionAbierta(sugerencia.ConversacionId) &&
            !RevisionRespuestaSugerida.EditadaLoSuficiente(_textoRespuesta, sugerencia.Texto))
            _textoRespuesta = string.Empty;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Antes de enviar: una sugerencia insertada en este redactor en esta apertura y editada más allá del umbral cuenta como
    /// revisada y se confirma en el servidor, que bloquea todo envío mientras siga sin revisar. Una sin revisar que no está en
    /// este redactor no se confirma con lo que se envía (revisión Codex r3, ALTO). Recibe el texto fijado al pulsar Enviar, no
    /// el del redactor cuando acabe de esperar, y la sugerencia del redactor de ese mismo momento. <c>false</c>: no se envía.
    /// </summary>
    private Task<bool> AsegurarSugerenciaRevisadaAsync(Guid conversacionId, string texto) =>
        _panelSugerencia is { } panel
            ? panel.ConfirmarSiEditadaAsync(conversacionId, texto, SugerenciaEnRedactorVigente)
            : Task.FromResult(true);

    private async Task AplicarMacro(string macroIdTexto)
    {
        if (string.IsNullOrWhiteSpace(macroIdTexto))
        {
            _macroSeleccionadaId = string.Empty;
            return;
        }
        if (_aplicandoMacro || _conversacionSeleccionadaId is not { } conversacionId ||
            !Guid.TryParse(macroIdTexto, out var macroId)) return;
        _aplicandoMacro = true;
        // Misma guarda que «Compartir formatos de centro» (CompartirFormatosCentroAsync): comparar solo la conversación deja pasar
        // A→B→A y un Cliente empresarial cambiado en una recarga durante la espera (revisión Codex r5 de la #2167, MEDIO 1). La
        // plantilla se rellenó con la conversación y el Cliente empresarial del contexto que la pidió; si ese contexto ya no es el
        // vigente (otra apertura, otra vista o una recarga que cambió el Cliente empresarial), no se añade al borrador.
        var vigencia = CapturarVigencia();
        try
        {
            var resultado = await Mediator.Send(new AplicarMacroCommand(conversacionId, macroId), _ciclo.Token);
            // Si llega con una recarga en curso, espera a saber con qué Cliente empresarial termina (ContextoConfirmadoAsync).
            if (!await ContextoConfirmadoAsync(vigencia))
            {
                // Misma apertura, contexto recargado: se avisa y se libera el selector.
                if (MismaApertura(vigencia))
                {
                    _macroSeleccionadaId = string.Empty;
                    ToastService.Mostrar(ErroresTextos["AvisoPlantillaContextoCambiado"].Value, TonoToast.Info);
                }
                return;
            }
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }
            _macroSeleccionadaId = macroIdTexto;
            var cuerpo = _detalle?.Canal == CanalConversacion.WhatsApp
                ? AvisoSinBuzonCorreo.HtmlATexto(resultado.Valor) ?? string.Empty
                : resultado.Valor;
            _textoRespuesta = string.IsNullOrWhiteSpace(_textoRespuesta)
                ? cuerpo
                : $"{_textoRespuesta.TrimEnd()}\n\n{cuerpo}";
            await CargarDetalleAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al aplicar la plantilla en la conversación {ConversacionId}.", conversacionId);
            ToastService.Mostrar("No se pudo aplicar la plantilla.", TonoToast.Error);
        }
        finally { _aplicandoMacro = false; }
    }

    /// <summary>Genera el resumen de documentación exigida por el Centro elegido y lo añade a la respuesta en curso — mismo patrón de prellenado que AplicarMacro.</summary>
    private async Task CompartirFormatosCentroAsync(string centroIdTexto)
    {
        _centroFormatosSeleccionado = centroIdTexto;
        // Toda elección es una petición nueva, también volver a «Selecciona un centro…»: la respuesta de una petición anterior
        // deja de ser la vigente aunque esta no llegue a salir (revisión Codex r5 de la #2167, MEDIO 2).
        var solicitud = ++_solicitudFormatosVigente;
        if (!Guid.TryParse(centroIdTexto, out var centroId)) return;

        // El resumen solo se añade al borrador del mismo contexto que lo pidió (revisión Codex r3 y r4 de la #2167, MEDIO 4).
        // Dos comprobaciones tras la espera:
        // - MismoContexto: cubre otra conversación, la misma reabierta (A→B→A), otra vista y un Cliente empresarial que cambió
        //   en una recarga durante la espera. El composer no se pinta mientras carga el detalle (Bandeja.razor, rama
        //   _cargandoDetalle), así que la petición no puede salir en mitad de una carga y su contexto es el que esa carga fijó.
        //   Pero una recarga sí puede EMPEZAR durante la espera, y el contexto solo cambia cuando esa recarga asigna el detalle:
        //   una respuesta que llega antes se retiene hasta que la recarga termina (ContextoConfirmadoAsync, revisión Codex r7).
        // - _solicitudFormatosVigente sube en cada petición: si el Gestor CAE elige otro Centro mientras espera, la respuesta
        //   del Centro anterior ya no es la vigente.
        var vigencia = CapturarVigencia();
        try
        {
            var formatos = await Mediator.Send(new ObtenerFormatosRequeridosCentroQuery(centroId), _ciclo.Token);
            if (solicitud != _solicitudFormatosVigente) return;
            // Mientras se espera a la recarga el composer no se pinta: no puede llegar otra elección de Centro.
            if (!await ContextoConfirmadoAsync(vigencia))
            {
                // Misma apertura, contexto recargado: se avisa y se libera el selector para que pueda volver a elegirse.
                if (MismaApertura(vigencia))
                {
                    _centroFormatosSeleccionado = string.Empty;
                    ToastService.Mostrar(ErroresTextos["AvisoFormatosContextoCambiado"].Value, TonoToast.Info);
                }
                return;
            }

            if (formatos is null)
            {
                ToastService.Mostrar("Este centro no tiene requisitos documentales configurados.", TonoToast.Info);
                return;
            }

            _textoRespuesta = string.IsNullOrWhiteSpace(_textoRespuesta) ? formatos : $"{_textoRespuesta}{formatos}";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al generar los formatos requeridos del centro {CentroId}.", centroId);
            ToastService.Mostrar(ErroresTextos["ErrorResumenDocumentacion"].Value, TonoToast.Error);
        }
    }

    /// <summary>
    /// Mismo tope que valida `ResponderConversacionCommand` del lado del
    /// servidor (`LimitesAdjuntosCorreo`) — comprobarlo aquí también evita
    /// que el usuario rellene el formulario entero antes de enterarse de
    /// que el conjunto de archivos no cabe.
    /// </summary>
    private async Task ManejarArchivosAdjuntosAsync(InputFileChangeEventArgs e)
    {
        // La lectura de los archivos espera al navegador: se hace sobre una copia y solo se incorpora al borrador si sigue siendo
        // el de la apertura que la empezó. Sin esto, los archivos elegidos en una conversación acababan en el borrador de otra, o
        // en el de la misma reabierta (A→B→A), porque la lista es la misma instancia (revisión Codex r5 de la #2167, inventario de
        // caminos asíncronos). Una recarga del detalle no cambia el borrador, así que no tira el archivo.
        var vigencia = CapturarVigencia();
        var lote = _adjuntosPendientes.ToList();
        var previos = lote.Count;
        var rechazo = await _cargaAdjuntos.AgregarAsync(e, lote);
        if (!MismaApertura(vigencia))
        {
            if (_conversacionSeleccionadaId == vigencia.ConversacionId)
                ToastService.Mostrar(ErroresTextos["AvisoAdjuntosContextoCambiado"].Value, TonoToast.Info);
            return;
        }

        // Solo lo leído en esta selección; si el Gestor CAE quitó un adjunto mientras tanto, no vuelve.
        _adjuntosPendientes.AddRange(lote.Skip(previos));
        _errorAdjuntos = AdjuntosCorreo.Mensaje(rechazo, ErroresTextos);
    }

    private void QuitarAdjuntoPendiente(AdjuntoParaEnviarDto adjunto)
    {
        _adjuntosPendientes.Remove(adjunto);
        _errorAdjuntos = null;
    }

    /// <summary>
    /// Único punto de envío del ComposerBar — decide el Command según el
    /// canal de la conversación abierta (§ 13.3: la respuesta sale por el
    /// canal de origen). El fallback WhatsApp→correo tiene su propio método
    /// (EnviarFallbackCorreoAsync) porque es un Command distinto.
    /// </summary>
    private async Task EnviarAsync()
    {
        // A prueba de doble clic: el segundo clic llega mientras el primero
        // espera al servidor y, hasta que ese await devuelva, el botón todavía
        // no se ha vuelto a pintar deshabilitado. Sin esta guarda salen DOS
        // correos idénticos al mismo destinatario, y un correo enviado no se
        // deshace.
        if (_enviando || _aplicandoMacro || VariablesPlantillaRespuesta.Pendientes(_textoRespuesta).Count > 0) return;
        if (_conversacionSeleccionadaId is not { } conversacionId || string.IsNullOrWhiteSpace(_textoRespuesta)) return;
        // Mientras carga otra conversación, el detalle aún es el de la anterior: no se envía con un canal que no es el suyo.
        if (_detalle is not { } detalle || detalle.Id != conversacionId) return;

        // Revisión Codex r1 (#2163, ALTO 3): destino, canal, texto y adjuntos se fijan aquí, antes de cualquier espera. Si
        // durante la confirmación de la sugerencia o el envío se abre otra conversación, sale lo que se pidió enviar en esta
        // y a esta, nunca el borrador de la otra; y lo de la otra no se borra al terminar (LimpiarLoEnviado).
        var esWhatsApp = detalle.Canal == CanalConversacion.WhatsApp;

        _enviando = true;
        // Instantánea exacta de lo que sale: con éxito se limpia solo eso (LimpiarLoEnviado). WhatsApp no lleva adjuntos.
        var enviado = new BorradorEnviado(CapturarVigencia(), _textoRespuesta,
            esWhatsApp ? [] : _adjuntosPendientes.ToList(), _errorAdjuntos, EmailFallback: null);
        try
        {
            if (!await AsegurarSugerenciaRevisadaAsync(conversacionId, enviado.Texto)) return;

            var resultado = esWhatsApp
                ? await Mediator.Send(new ResponderConversacionWhatsAppCommand(conversacionId, enviado.Texto.Trim()), _ciclo.Token)
                : await Mediator.Send(new ResponderConversacionCommand(
                    conversacionId, enviado.Texto, enviado.Adjuntos.Count > 0 ? enviado.Adjuntos.ToList() : null), _ciclo.Token);

            if (resultado.EsFallido)
            {
                // Fallo de negocio: se dice cuál, y NO se toca lo escrito ni
                // los adjuntos — el gestor corrige y vuelve a darle a Enviar
                // sin reescribir la respuesta entera.
                ToastService.MostrarError(resultado.Error);
                return;
            }

            // Se vacía lo que se envió, no lo que haya ahora (LimpiarLoEnviado): un adjunto añadido o una dirección cambiada
            // mientras viajaba el envío se conservan, y otra apertura (otra conversación, o la misma reabierta con el mismo texto)
            // no se toca (revisión Codex r6 de la #2167, MEDIO 2). No se compara la carga del detalle: una recarga por un mensaje
            // entrante dejaría lo enviado en el composer, listo para mandarse dos veces.
            LimpiarLoEnviado(enviado);
            ToastService.Mostrar("Respuesta enviada.", TonoToast.Exito);

            // El medidor y el detalle son los de la conversación pintada: con otra apertura, solo la lista. Se comprueba tras cada
            // espera, como en CambiarEstadoAsync: si mientras el medidor cierra el tramo se abre otra conversación, recargarla
            // desmontaría su nota interna a medias (revisión Codex r7 de la #2167, MEDIO 2).
            if (MismaApertura(enviado.Vigencia))
            {
                await RegistrarAccionMedidaAsync();
                if (MismaApertura(enviado.Vigencia)) await CargarDetalleAsync();
            }
            await CargarListaAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ValidationException ex)
        {
            // Desenlace distinto del fallo genérico: aquí el servidor SÍ dice
            // qué está mal (el tope de los adjuntos, el cuerpo vacío…), así que
            // se enseña su mensaje en vez de "intenta nuevamente", que invita a
            // repetir lo mismo y volver a fallar igual.
            ToastService.Mostrar(string.Join(" ", ex.Errors.Select(e => e.ErrorMessage).Distinct()), TonoToast.Error);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al responder en la conversación {ConversacionId}.", conversacionId);
            ToastService.Mostrar(ErroresTextos["ErrorEnviarRespuesta"].Value, TonoToast.Error);
        }
        finally
        {
            _enviando = false;
        }
    }

    /// <summary>Fallback de canal (§ 16.5): WhatsApp con la ventana de 24h cerrada continúa el MISMO hilo por correo.</summary>
    private async Task EnviarFallbackCorreoAsync()
    {
        if (_enviando || _aplicandoMacro || VariablesPlantillaRespuesta.Pendientes(_textoRespuesta).Count > 0) return;
        if (_conversacionSeleccionadaId is not { } conversacionId || string.IsNullOrWhiteSpace(_textoRespuesta) || string.IsNullOrWhiteSpace(_emailFallback))
            return;
        if (_detalle?.Id != conversacionId) return;

        // Como en EnviarAsync (revisión Codex r1, ALTO 3): lo que se envía y a dónde se fija antes de esperar.
        _enviando = true;
        // El fallback no lleva adjuntos: la instantánea es el texto y la dirección.
        var enviado = new BorradorEnviado(CapturarVigencia(), _textoRespuesta, [], ErrorAdjuntos: null, _emailFallback);
        try
        {
            if (!await AsegurarSugerenciaRevisadaAsync(conversacionId, enviado.Texto)) return;

            var resultado = await Mediator.Send(new MigrarConversacionACorreoCommand(
                conversacionId, enviado.EmailFallback!.Trim(), enviado.Texto), _ciclo.Token);
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            // Ver EnviarAsync: solo se vacía lo que se envió (una dirección nueva escrita durante el envío se conserva).
            LimpiarLoEnviado(enviado);
            ToastService.Mostrar("Mensaje enviado por correo — la conversación continúa en este mismo hilo.", TonoToast.Exito);

            if (MismaApertura(enviado.Vigencia)) await CargarDetalleAsync();
            await CargarListaAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ValidationException ex)
        {
            ToastService.Mostrar(string.Join(" ", ex.Errors.Select(e => e.ErrorMessage).Distinct()), TonoToast.Error);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al migrar a correo la conversación {ConversacionId}.", conversacionId);
            ToastService.Mostrar(ErroresTextos["ErrorEnviarCorreoFallback"].Value, TonoToast.Error);
        }
        finally
        {
            _enviando = false;
        }
    }

    private IJSObjectReference? _moduloClipboard;

    /// <summary>
    /// "Copiar enlace" (§ 7 punto 3 y Horizonte 2.6 de
    /// Project-Hydra-Negocio/MACRO_PLAN_2026-08-13.md): con "conversacion" ya sincronizado en la
    /// URL por SeleccionarConversacionAsync, la URL actual del navegador ya
    /// es el deep-link — mismo criterio que ContextWorkspace.CopiarEnlaceAsync
    /// y mismo módulo clipboard.js que BotonCopiar.
    /// </summary>
    private async Task CopiarEnlaceAsync()
    {
        try
        {
            _moduloClipboard ??= await JsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/clipboard.js");
            await _moduloClipboard.InvokeVoidAsync("copiarAlPortapapeles", NavigationManager.Uri);
            ToastService.Mostrar("Se copió el enlace a esta conversación al portapapeles.", TonoToast.Exito);
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos copiar el enlace.", TonoToast.Error);
        }
    }

    /// <summary>
    /// Medidor de tiempo de enfoque de la conversación abierta. Null mientras no hay
    /// hilo seleccionado, y sin efecto si la medición está apagada para el tenant.
    /// </summary>
    private MedidorTiempoGestion? _medidorTiempo;

    /// <summary>
    /// Cierra el tramo de medición con motivo "acción completada". Se llama tras cada
    /// acción real del Gestor sobre el hilo — enviar, cambiar estado, confirmar una
    /// sugerencia —, que es justo el evento que la propuesta define como fin de bloque.
    /// </summary>
    private Task RegistrarAccionMedidaAsync() =>
        _medidorTiempo?.RegistrarAccionCompletadaAsync() ?? Task.CompletedTask;

    /// <summary>
    /// Recrea el selector tras cada intento para restaurar el estado real cuando el comando falla.
    /// </summary>
    private int _intentosCambioEstado;

    private async Task CambiarEstadoDesdeSelectorAsync(string valor)
    {
        if (!Enum.TryParse<EstadoConversacion>(valor, out var nuevoEstado) || !Enum.IsDefined(nuevoEstado)
            || nuevoEstado == _detalle?.Estado || nuevoEstado == EstadoConversacion.Cerrada)
            return;

        try
        {
            await CambiarEstadoAsync(nuevoEstado);
        }
        finally
        {
            _intentosCambioEstado++;
        }
    }

    private async Task CambiarEstadoAsync(EstadoConversacion nuevoEstado)
    {
        if (_conversacionSeleccionadaId is not { } id || _cambiandoEstado) return;

        // La sucesora se fija por identidad ANTES de enviar el comando: la siguiente fila de la lista
        // que el Gestor CAE ve ahora (mismos filtros y Asignación de Cartera). Tras recargar, ni el
        // índice ni el orden son fiables: la fila puede haber salido del filtro y otra, entrar.
        var filasAntes = ConversacionesVisiblesEnOrden();
        var indiceAnterior = filasAntes.FindIndex(c => c.Id == id);
        Guid? sucesoraId = indiceAnterior >= 0 && indiceAnterior + 1 < filasAntes.Count
            ? filasAntes[indiceAnterior + 1].Id
            : null;

        _cambiandoEstado = true;
        var vigencia = CapturarVigencia();
        try
        {
            var resultado = await Mediator.Send(new CambiarEstadoConversacionCommand(id, nuevoEstado), _ciclo.Token);
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            // Solo importa si el Gestor CAE navegó a otra conversación (o reabrió la misma: comparar el id dejaba pasar A→B→A
            // y saltar a la sucesora desde la reapertura) mientras el comando viajaba. Los refrescos de fondo de la lista no son
            // motivo para dejar de registrar ni de recargar.
            if (!MismaApertura(vigencia))
            {
                await CargarListaAsync();
                return;
            }

            await RegistrarAccionMedidaAsync();
            if (!MismaApertura(vigencia))
            {
                await CargarListaAsync();
                return;
            }

            await CargarDetalleAsync();
            var listaFresca = await RecargarListaFrescaAsync();
            if (!MismaApertura(vigencia) || !listaFresca) return;
            if (nuevoEstado is EstadoConversacion.Resuelta or EstadoConversacion.Cerrada
                && sucesoraId is { } sucesora && _conversaciones.Any(c => c.Id == sucesora))
                await SeleccionarConversacionAsync(sucesora);
        }
        finally
        {
            _cambiandoEstado = false;
        }
    }

    private async Task CambiarEjecutivoAsync(string ejecutivoIdTexto)
    {
        _ejecutivoSeleccionado = ejecutivoIdTexto;
        if (_conversacionSeleccionadaId is null) return;

        _cambiandoEjecutivo = true;
        try
        {
            var ejecutivoId = Guid.TryParse(ejecutivoIdTexto, out var id) ? id : (Guid?)null;
            var resultado = await Mediator.Send(new AsignarEjecutivoConversacionCommand(_conversacionSeleccionadaId.Value, ejecutivoId), _ciclo.Token);
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            await CargarListaAsync();
        }
        finally
        {
            _cambiandoEjecutivo = false;
        }
    }

    private async Task AsignarClienteTriageAsync()
    {
        if (_conversacionSeleccionadaId is null || !Guid.TryParse(_clienteTriageSeleccionado, out var clienteId)) return;

        _asignandoCliente = true;
        var vigencia = CapturarVigencia();
        try
        {
            var resultado = await Mediator.Send(new AsignarClienteConversacionCommand(_conversacionSeleccionadaId.Value, clienteId), _ciclo.Token);
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            ToastService.Mostrar("Conversación asignada al Cliente.", TonoToast.Exito);
            // Mismo criterio que el resto de acciones sobre el hilo: con otra apertura, solo la lista.
            if (MismaApertura(vigencia)) await CargarDetalleAsync();
            await CargarListaAsync();
        }
        finally
        {
            _asignandoCliente = false;
        }
    }

    /// <summary>
    /// Navega a /visitas con la sugerencia y, si el gestor corrigió Centro o
    /// fechas en la revisión del Action Center, los overrides correspondientes
    /// — ver comentario de CentroIdOverride en Visitas.razor.cs.
    /// </summary>
    private void IrACrearVisitaDesdeSugerencia(
        Guid sugerenciaId, Guid? centroIdCorregido = null, DateOnly? fechaInicioCorregida = null, DateOnly? fechaFinCorregida = null)
    {
        var query = $"sugerenciaId={sugerenciaId}";
        if (centroIdCorregido is not null) query += $"&centroId={centroIdCorregido}";
        if (fechaInicioCorregida is not null) query += $"&fechaInicio={fechaInicioCorregida:yyyy-MM-dd}";
        if (fechaFinCorregida is not null) query += $"&fechaFin={fechaFinCorregida:yyyy-MM-dd}";

        NavigationManager.NavigateTo($"/visitas?{query}");
    }

    private async Task DescartarSugerenciaVisitaAsync(Guid sugerenciaId)
    {
        var vigencia = CapturarVigencia();
        try
        {
            var resultado = await Mediator.Send(new DescartarSugerenciaVisitaCorreoCommand(sugerenciaId), _ciclo.Token);
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            if (MismaApertura(vigencia) && _conversacionSeleccionadaId is not null)
                await CargarDetalleAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al descartar la sugerencia de visita {SugerenciaId}.", sugerenciaId);
            ToastService.Mostrar(ErroresTextos["ErrorDescartarSugerencia"].Value, TonoToast.Error);
        }
    }

    /// <summary>
    /// A diferencia de "Crear visita" (que abre /visitas a que el Gestor
    /// complete fecha y confirme), aquí no hace falta ningún dato adicional
    /// del Gestor: Trabajador y TipoDocumento ya los resolvió la IA, y los
    /// Centros salen de las Asignaciones activas del propio Trabajador — el
    /// clic en el botón ya es la confirmación explícita exigida.
    /// </summary>
    private async Task GenerarGestionesDesdeSugerenciaAsync(Guid sugerenciaId, Guid trabajadorId, Guid tipoDocumentoId)
    {
        var vigencia = CapturarVigencia();
        try
        {
            var resultado = await Mediator.Send(new CrearGestionesParaTrabajadorCommand(trabajadorId, tipoDocumentoId, sugerenciaId), _ciclo.Token);
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            ToastService.Mostrar($"Se generaron {resultado.Valor.Creadas} gestión(es).", TonoToast.Exito);

            if (MismaApertura(vigencia) && _conversacionSeleccionadaId is not null)
                await CargarDetalleAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al generar gestiones desde la sugerencia {SugerenciaId}.", sugerenciaId);
            ToastService.Mostrar(ErroresTextos["ErrorGenerarGestiones"].Value, TonoToast.Error);
        }
    }

    /// <summary>
    /// Confirma la propuesta del Conversation Matching Engine (§ 13.2):
    /// fusiona la conversación WhatsApp abierta en la conversación elegida y
    /// selecciona el resultado — el hilo original (ahora Cerrada, sin
    /// mensajes propios) deja de tener sentido seguir viendo.
    /// </summary>
    private async Task VincularConversacionAsync(Guid conversacionDestinoId)
    {
        if (_conversacionSeleccionadaId is not { } conversacionOrigenId) return;

        var vigencia = CapturarVigencia();
        try
        {
            var resultado = await Mediator.Send(new VincularConversacionCommand(conversacionOrigenId, conversacionDestinoId), _ciclo.Token);
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            ToastService.Mostrar("Conversaciones vinculadas.", TonoToast.Exito);
            // Si mientras se vinculaba el Gestor CAE abrió otra conversación (o reabrió esta), no se le lleva al destino: se
            // quedaría sin la que eligió (inventario de la revisión Codex r6 de la #2167).
            if (MismaApertura(vigencia)) await SeleccionarConversacionAsync(conversacionDestinoId);
            await CargarListaAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al vincular la conversación {Origen} con {Destino}.", conversacionOrigenId, conversacionDestinoId);
            ToastService.Mostrar(ErroresTextos["ErrorVincularConversacion"].Value, TonoToast.Error);
        }
    }

    /// <summary>El gestor confirma que un mensaje marcado como ruido (ronda de reducción de ruido en Comunicaciones) sí importa — deja de tratarse como tal.</summary>
    private async Task ConfirmarRuidoAsync(Guid mensajeId)
    {
        var vigencia = CapturarVigencia();
        try
        {
            var resultado = await Mediator.Send(new ConfirmarClasificacionRuidoMensajeCommand(mensajeId), _ciclo.Token);
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            if (MismaApertura(vigencia)) await CargarDetalleAsync();
            await CargarListaAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al confirmar la clasificación de ruido del mensaje {MensajeId}.", mensajeId);
            ToastService.Mostrar(ErroresTextos["ErrorConfirmarRuido"].Value, TonoToast.Error);
        }
    }

    private async Task DescartarSugerenciaGestionAsync(Guid sugerenciaId)
    {
        var vigencia = CapturarVigencia();
        try
        {
            var resultado = await Mediator.Send(new DescartarSugerenciaGestionCorreoCommand(sugerenciaId), _ciclo.Token);
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            if (MismaApertura(vigencia) && _conversacionSeleccionadaId is not null)
                await CargarDetalleAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al descartar la sugerencia de gestión {SugerenciaId}.", sugerenciaId);
            ToastService.Mostrar(ErroresTextos["ErrorDescartarSugerencia"].Value, TonoToast.Error);
        }
    }

    /// <summary>
    /// Abre el flujo "Actualizar documentación desde conversación"
    /// (Project-Hydra-Negocio/tecnico/docs/COMUNICACIONES.md § 12.7) para un adjunto concreto. Carga los
    /// selectores y lanza la detección en el mismo gesto — si
    /// ExtraccionDocumentoAdjuntoOptions está apagado (por defecto), la
    /// detección vuelve vacía y el gestor rellena los campos a mano.
    /// </summary>
    private async Task AbrirActualizarDocumentoAsync(Guid adjuntoId)
    {
        _adjuntoParaActualizarDocumentoId = adjuntoId;
        _modalActualizarDocumentoVisible = true;
        _actualizarDocumentoContextoCaducado = false;
        _errorActualizarDocumento = null;
        _tipoDocumentoIdFormulario = string.Empty;
        _propietarioDocumentoFormulario = nameof(AmbitoAplicacion.Trabajador);
        _trabajadorDocumentoIdFormulario = string.Empty;
        _empresaDocumentoIdFormulario = string.Empty;
        _fechaEmisionDocumentoFormulario = FechaDeHoy.ParaCampoFecha(Reloj);
        _fechaVencimientoDocumentoFormulario = string.Empty;
        _vencimientoDocumentoNoValido = false;
        _emisionDocumentoNoValida = false;
        _noCaducaDocumentoFormulario = false;
        _comentariosDocumentoFormulario = string.Empty;
        _confianzaDeteccionDocumento = 0;
        _aplicandoDocumento = false;
        FijarInstantaneaActualizarDocumento();

        _detectandoDocumento = true;
        StateHasChanged();

        // Lo que llega tras cada espera solo se escribe si esta apertura del modal sigue siendo la vigente. Cerrarlo, reabrirlo o
        // cambiar el contexto (otra apertura de la conversación, otra vista, una recarga que cambia el Cliente empresarial) sube
        // _solicitudActualizarDocumentoVigente: CambiarContexto cierra el modal y avisa (revisión Codex r5 y r6 de la #2167). Una
        // recarga que no cambia el Cliente empresarial no lo cierra: las Empresas y lo detectado siguen siendo de su contexto.
        var solicitud = ++_solicitudActualizarDocumentoVigente;
        var clienteEmpresas = _detalle?.ClienteId;
        bool Vigente() => solicitud == _solicitudActualizarDocumentoVigente;

        try
        {
            var tipos = await Mediator.Send(new ObtenerTiposDocumentoQuery(ParaAltaNueva: true), _ciclo.Token);
            if (!Vigente()) return;
            _tiposDocumentoSelector = tipos;

            var trabajadores = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery(AlcanceSelectorTrabajadores.Cartera), _ciclo.Token);
            if (!Vigente()) return;
            _trabajadoresSelector = trabajadores;

            var empresas = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery(clienteEmpresas), _ciclo.Token);
            if (!Vigente()) return;
            _empresasSelector = empresas;

            var deteccion = await Mediator.Send(new DetectarActualizacionDocumentoDesdeAdjuntoQuery(adjuntoId), _ciclo.Token);
            if (!Vigente()) return;
            if (deteccion.EsFallido)
            {
                _errorActualizarDocumento = deteccion.Error.Mensaje;
                return;
            }

            var valor = deteccion.Valor;
            _confianzaDeteccionDocumento = valor.ConfianzaGeneral;
            if (valor.TipoDocumentoId is { } tipoDocumentoId) _tipoDocumentoIdFormulario = tipoDocumentoId.ToString();
            if (valor.Ambito == AmbitoAplicacion.Empresa) _propietarioDocumentoFormulario = nameof(AmbitoAplicacion.Empresa);
            if (valor.TrabajadorId is { } trabajadorId) _trabajadorDocumentoIdFormulario = trabajadorId.ToString();
            if (valor.EmpresaId is { } empresaId) _empresaDocumentoIdFormulario = empresaId.ToString();
            if (valor.FechaEmision is { } fechaEmision) _fechaEmisionDocumentoFormulario = fechaEmision.ToString("yyyy-MM-dd");
            if (valor.FechaVencimiento is { } fechaVencimiento) _fechaVencimientoDocumentoFormulario = fechaVencimiento.ToString("yyyy-MM-dd");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al detectar la actualización de documentación del adjunto {AdjuntoId}.", adjuntoId);
            if (Vigente())
                _errorActualizarDocumento = "No pudimos analizar el adjunto. Puedes rellenar los campos a mano.";
        }
        finally
        {
            if (Vigente())
            {
                _detectandoDocumento = false;
                // Lo que rellenó la detección no es un cambio de quien revisa.
                FijarInstantaneaActualizarDocumento();
            }
            StateHasChanged();
        }
    }

    /// <summary>
    /// Cierra el modal y termina su apertura: lo que aún espere de ella (detección, aplicar) deja de escribir en el modal. Lo
    /// usan «Cancelar» y la X, el descarte de cambios y CambiarContexto.
    /// </summary>
    private void CerrarModalActualizarDocumento()
    {
        _modalActualizarDocumentoVisible = false;
        _detectandoDocumento = false;
        _actualizarDocumentoContextoCaducado = false;
        ++_solicitudActualizarDocumentoVigente;
    }

    /// <summary>
    /// El modal sigue abierto con lo que el Gestor CAE escribió, pero su contexto ya no es el de la conversación (CambiarContexto):
    /// «Aplicar» queda bloqueado con este motivo hasta que lo cierre.
    /// </summary>
    private bool _actualizarDocumentoContextoCaducado;

    private string? MotivoAplicarDocumentoDeshabilitado =>
        _actualizarDocumentoContextoCaducado ? ErroresTextos["AvisoActualizarDocumentoContextoCaducado"].Value : null;

    // El mismo motivo, a la vista dentro del modal (el title del botón no basta para explicar por qué no se aplica).
    private RenderFragment? AvisoActualizarDocumentoCaducado =>
        MotivoAplicarDocumentoDeshabilitado is { } motivo ? AvisoFormularioCon(motivo) : null;

    private static RenderFragment AvisoFormularioCon(string mensaje) => builder =>
    {
        builder.OpenComponent<AvisoFormulario>(0);
        builder.AddAttribute(1, nameof(AvisoFormulario.Mensaje), mensaje);
        builder.CloseComponent();
    };

    // Al volver a ser una fecha entera (o quedar vacía), el aviso «no es una fecha» de un envío anterior ya no es cierto.
    private void AlCambiarEmisionDocumentoNoValida(bool noValida)
    {
        _emisionDocumentoNoValida = noValida;
        if (!noValida && !_vencimientoDocumentoNoValido && _errorActualizarDocumento == TextosComunesPagina["CampoFechaCorregirAntes"])
            _errorActualizarDocumento = null;
    }

    private void AlCambiarVencimientoDocumentoNoValido(bool noValido)
    {
        _vencimientoDocumentoNoValido = noValido;
        if (!noValido && !_emisionDocumentoNoValida && _errorActualizarDocumento == TextosComunesPagina["CampoFechaCorregirAntes"])
            _errorActualizarDocumento = null;
    }

    private async Task AplicarActualizacionDocumentoAsync()
    {
        // Con el contexto caducado el botón ya está deshabilitado; esto cubre cualquier otra vía de envío del formulario.
        if (_actualizarDocumentoContextoCaducado) return;

        if (!Guid.TryParse(_tipoDocumentoIdFormulario, out var tipoDocumentoId))
        {
            _errorActualizarDocumento = "Selecciona un tipo de documento.";
            return;
        }

        // Una emisión a medias ya llega como «» (no se calcula el vencimiento de la anterior); la marca pide corregirla.
        if (_emisionDocumentoNoValida || !DateOnly.TryParse(_fechaEmisionDocumentoFormulario, out var fechaEmision))
        {
            _errorActualizarDocumento = _emisionDocumentoNoValida ? TextosComunesPagina["CampoFechaCorregirAntes"] : "Indica la fecha de emisión.";
            return;
        }

        var esTrabajador = _propietarioDocumentoFormulario == nameof(AmbitoAplicacion.Trabajador);
        Guid? trabajadorId = esTrabajador && Guid.TryParse(_trabajadorDocumentoIdFormulario, out var idTrabajador) ? idTrabajador : null;
        Guid? empresaId = !esTrabajador && Guid.TryParse(_empresaDocumentoIdFormulario, out var idEmpresa) ? idEmpresa : null;

        if (trabajadorId is null && empresaId is null)
        {
            _errorActualizarDocumento = esTrabajador ? "Selecciona un trabajador." : "Selecciona una empresa.";
            return;
        }

        // Un vencimiento a medias o ilegible llega como «»: sin este bloqueo el documento se actualizaría «sin vencimiento».
        if (_vencimientoDocumentoNoValido)
        {
            _errorActualizarDocumento = TextosComunesPagina["CampoFechaCorregirAntes"];
            return;
        }

        DateOnly? fechaVencimientoManual = DateOnly.TryParse(_fechaVencimientoDocumentoFormulario, out var fv) ? fv : null;
        if (fechaVencimientoManual is not null && _noCaducaDocumentoFormulario)
        {
            _errorActualizarDocumento = TextosVigenciaDocumento.Texto("ErrorFechaYNoCaduca");
            return;
        }

        _aplicandoDocumento = true;
        _errorActualizarDocumento = null;
        // El error y el cierre son de esta apertura del modal; si un cambio de contexto lo cerró (y quizá se reabrió con otro
        // adjunto) mientras viajaba el comando, no se escriben en el modal que haya ahora. La validación sigue siendo del servidor.
        var solicitud = _solicitudActualizarDocumentoVigente;
        var vigencia = CapturarVigencia();
        var adjuntoId = _adjuntoParaActualizarDocumentoId;
        try
        {
            var resultado = await Mediator.Send(new ActualizarDocumentoDesdeAdjuntoCommand(
                adjuntoId, tipoDocumentoId, trabajadorId, empresaId, fechaEmision, fechaVencimientoManual,
                string.IsNullOrWhiteSpace(_comentariosDocumentoFormulario) ? null : _comentariosDocumentoFormulario,
                _noCaducaDocumentoFormulario), _ciclo.Token);

            if (resultado.EsFallido)
            {
                if (solicitud == _solicitudActualizarDocumentoVigente) _errorActualizarDocumento = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Documentación actualizada.", TonoToast.Exito);
            if (solicitud == _solicitudActualizarDocumentoVigente) CerrarModalActualizarDocumento();
            if (MismaApertura(vigencia)) await CargarDetalleAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al actualizar la documentación desde el adjunto {AdjuntoId}.", adjuntoId);
            if (solicitud == _solicitudActualizarDocumentoVigente) _errorActualizarDocumento = ErroresTextos["ErrorActualizarDocumento"].Value;
        }
        finally
        {
            // Con el modal reabierto, el indicador es de la apertura nueva (que lo puso a false al abrirse) y no se toca.
            if (!_modalActualizarDocumentoVisible || solicitud == _solicitudActualizarDocumentoVigente) _aplicandoDocumento = false;
        }
    }

    /// <summary>Ver DocumentoCitadoDetalleDto — el tono/etiqueta hace visible el nivel de certeza, no solo agrupa.</summary>
    private static TonoBadge NivelBadgeTono(NivelConfianzaDocumentoCitado nivel) => nivel switch
    {
        NivelConfianzaDocumentoCitado.Confirmado => TonoBadge.Exito,
        NivelConfianzaDocumentoCitado.CandidatoIA => TonoBadge.Advertencia,
        _ => TonoBadge.Neutro
    };

    private static string NivelBadgeEtiqueta(NivelConfianzaDocumentoCitado nivel) => nivel switch
    {
        NivelConfianzaDocumentoCitado.Confirmado => "Confirmado",
        NivelConfianzaDocumentoCitado.CandidatoIA => "Posible (IA)",
        _ => "Del propietario"
    };

    /// <summary>Dos letras de la razón social para el avatar de la columna de contexto — mismo criterio que FilaConversacion.ObtenerIniciales.</summary>
    private static string ObtenerInicialesCliente(string razonSocial)
    {
        var palabras = razonSocial.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return palabras.Length >= 2
            ? $"{palabras[0][0]}{palabras[1][0]}".ToUpperInvariant()
            : razonSocial.Length >= 2 ? razonSocial[..2].ToUpperInvariant() : razonSocial.ToUpperInvariant();
    }

    // --- Redactar (Parte XVI PROMPT 09) — ver comentario del Drawer en el .razor ---

    private bool _redactarVisible;
    private IReadOnlyList<ConexionIntegracionListaDto> _conexionesRedactar = [];
    private Guid? _conexionRedactarId;
    private string _redactarDestinatarios = string.Empty;
    private string _redactarAsunto = string.Empty;
    private string _redactarCuerpo = string.Empty;
    private string? _redactarError;
    private bool _enviandoRedaccion;

    private async Task AbrirRedactarAsync()
    {
        _redactarDestinatarios = string.Empty;
        _redactarAsunto = string.Empty;
        _redactarCuerpo = string.Empty;
        _redactarError = null;

        var conexiones = await Mediator.Send(new ObtenerConexionesIntegracionQuery(), _ciclo.Token);
        _conexionesRedactar = conexiones
            .Where(c => c.Estado == EstadoConexionIntegracion.Habilitada && c.GestorPropietarioId == null)
            .ToList();
        _conexionRedactarId = _conexionesRedactar.Count == 1 ? _conexionesRedactar[0].Id : null;
        _instantaneaRedactar.Fijar(ValoresRedactar());

        // FS-15: sin buzón el Drawer se abre igualmente, con el aviso que dice por qué y quién lo conecta
        // (AvisoSinBuzonCorreo) en lugar del formulario. Antes era un toast que se iba solo.
        _redactarVisible = true;
    }

    // --- Pedir prioridad (Parte XVI PROMPT 09) — mismo comando que CentroWorkspacePanel, aquí sobre _centroFormatosSeleccionado ---

    private bool _drawerPrioridadVisible;
    private bool _cargandoBorradorPrioridad;
    private bool _enviandoPrioridad;
    private string? _mensajeErrorPrioridad;
    private BorradorPedirPrioridadDto? _borradorPrioridad;
    private string _destinatarioPrioridad = string.Empty;
    private string _asuntoPrioridad = string.Empty;
    private string _cuerpoPrioridad = string.Empty;

    /// <summary>Centro para el que se preparó el borrador abierto en el Drawer de «Pedir prioridad».</summary>
    private Guid? _centroPrioridadId;

    private async Task AbrirPedirPrioridadAsync()
    {
        if (CentroSeleccionadoVigente is not { } centroId) return;

        // Cada apertura es una solicitud nueva; cerrar el Drawer también la termina (CerrarDrawerPrioridad). Cancelar y reabrir
        // para el MISMO Centro deja la primera respuesta sin dueño: no sobrescribe la segunda ni apaga su indicador de carga
        // (revisión Codex r6 de la #2167, MEDIO 3). Comparar el Centro no lo distinguía.
        var solicitud = ++_solicitudPrioridadVigente;
        _centroPrioridadId = centroId;
        _drawerPrioridadVisible = true;
        _cargandoBorradorPrioridad = true;
        _mensajeErrorPrioridad = null;
        _borradorPrioridad = null;
        _instantaneaPrioridad.Fijar(ValoresPrioridad());

        try
        {
            var resultado = await Mediator.Send(new ObtenerBorradorPedirPrioridadQuery(centroId), _ciclo.Token);
            if (solicitud != _solicitudPrioridadVigente) return;
            // El contexto del borrador es su Centro: CentroSeleccionadoVigente deja de ser este en cuanto una recarga cambia el
            // Cliente empresarial (o mientras lo recarga) y en cuanto se abre otra conversación, así que no hace falta otra
            // generación de contexto aquí. Su borrador no se ofrece.
            if (CentroSeleccionadoVigente != centroId)
            {
                _mensajeErrorPrioridad = ErroresTextos["AvisoPrioridadCentroRetirado"].Value;
                return;
            }
            if (resultado.EsFallido)
            {
                _mensajeErrorPrioridad = resultado.Error.Mensaje;
                return;
            }

            _borradorPrioridad = resultado.Valor;
            _destinatarioPrioridad = resultado.Valor.DestinatarioSugerido ?? string.Empty;
            _asuntoPrioridad = resultado.Valor.Asunto;
            _cuerpoPrioridad = resultado.Valor.CuerpoHtml;
            // El borrador propuesto no es un cambio de quien lo revisa.
            _instantaneaPrioridad.Fijar(ValoresPrioridad());
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al preparar el borrador de pedir prioridad para el centro {CentroId}.", centroId);
            if (solicitud == _solicitudPrioridadVigente) _mensajeErrorPrioridad = ErroresTextos["ErrorBorradorPrioridad"].Value;
        }
        finally
        {
            // El indicador es de la solicitud vigente: la de una apertura anterior no lo apaga antes de tiempo.
            if (solicitud == _solicitudPrioridadVigente) _cargandoBorradorPrioridad = false;
        }
    }

    /// <summary>«Cancelar», la X y el descarte de cambios: cerrar el Drawer termina su solicitud.</summary>
    private void CerrarDrawerPrioridad()
    {
        _drawerPrioridadVisible = false;
        _cargandoBorradorPrioridad = false;
        ++_solicitudPrioridadVigente;
    }

    private async Task EnviarPrioridadAsync()
    {
        // Ver EnviarAsync: dos clics no pueden pedir prioridad dos veces.
        if (_enviandoPrioridad) return;
        // Solo sobre el Centro del borrador y mientras siga entre los del contexto vigente: una recarga que cambia el Cliente
        // empresarial con el Drawer abierto lo retira (revisión Codex r5 de la #2167, MEDIO 3).
        if (_centroPrioridadId is not { } centroId || CentroSeleccionadoVigente != centroId)
        {
            _mensajeErrorPrioridad = ErroresTextos["AvisoPrioridadCentroRetirado"].Value;
            return;
        }

        var destinatarios = _destinatarioPrioridad
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (destinatarios.Count == 0)
        {
            _mensajeErrorPrioridad = "Indica al menos un destinatario.";
            return;
        }

        _enviandoPrioridad = true;
        _mensajeErrorPrioridad = null;
        // El error y el cierre son de esta apertura del Drawer, no de una posterior.
        var solicitud = _solicitudPrioridadVigente;

        try
        {
            var resultado = await Mediator.Send(
                new PedirPrioridadValidacionCommand(centroId, destinatarios, _asuntoPrioridad, _cuerpoPrioridad), _ciclo.Token);

            if (resultado.EsFallido)
            {
                if (solicitud == _solicitudPrioridadVigente) _mensajeErrorPrioridad = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Solicitud de prioridad enviada.", TonoToast.Exito);
            if (solicitud == _solicitudPrioridadVigente) CerrarDrawerPrioridad();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ValidationException ex)
        {
            if (solicitud == _solicitudPrioridadVigente)
                _mensajeErrorPrioridad = string.Join(" ", ex.Errors.Select(e => e.ErrorMessage).Distinct());
        }
        finally
        {
            _enviandoPrioridad = false;
        }
    }

    private async Task EnviarMensajeNuevoDesdeRedactarAsync()
    {
        // Ver EnviarAsync: mismo motivo, y aquí el doble envío es aún más
        // visible porque el destinatario recibe dos correos nuevos, no dos
        // mensajes dentro de un hilo que ya conocía.
        if (_enviandoRedaccion) return;

        if (_conexionRedactarId is null)
        {
            _redactarError = "Selecciona desde qué buzón enviarlo.";
            return;
        }

        var destinatarios = _redactarDestinatarios
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (destinatarios.Count == 0)
        {
            _redactarError = "Indica al menos un destinatario.";
            return;
        }

        _enviandoRedaccion = true;
        _redactarError = null;
        try
        {
            var resultado = await Mediator.Send(new EnviarMensajeNuevoCommand(
                _conexionRedactarId.Value, destinatarios, _redactarAsunto, _redactarCuerpo), _ciclo.Token);

            if (resultado.EsFallido)
            {
                // El formulario NO se cierra: destinatarios, asunto y cuerpo
                // siguen ahí para corregir y reintentar.
                _redactarError = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Mensaje enviado.", TonoToast.Exito);
            _redactarVisible = false;
            await AplicarFiltrosAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ValidationException ex)
        {
            // Tercer desenlace: el servidor dice qué campo está mal. Decirlo
            // tal cual, y no "intenta nuevamente" — que aquí sería falso: el
            // mismo intento volvería a fallar igual.
            _redactarError = string.Join(" ", ex.Errors.Select(e => e.ErrorMessage).Distinct());
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al enviar un mensaje nuevo desde la conexión {ConexionId}.", _conexionRedactarId);
            _redactarError = ErroresTextos["ErrorEnviarMensajeNuevo"].Value;
        }
        finally
        {
            _enviandoRedaccion = false;
        }
    }

    // --- Aviso de cambios sin guardar (P1-E2b) ---

    private readonly InstantaneaFormulario _instantaneaActualizarDocumento = new();
    private readonly InstantaneaFormulario _instantaneaRedactar = new();
    private readonly InstantaneaFormulario _instantaneaPrioridad = new();
    private ComposerNotaInterna? _composerNota;
    private bool _navegandoDentroDeLaPagina;

    /// <summary>
    /// La conversación abierta como zona que se desmonta sin navegar: elegir otra o cambiar a
    /// «Mi buzón personal» pregunta con el mismo aviso antes de hacerlo (ConfirmarAbandonoAsync).
    /// </summary>
    private readonly AmbitoCambiosSinGuardar _ambitoConversacion = new();

    /// <summary>
    /// Escribir la nota solo re-renderiza el composer: este manejador vacío hace que la página
    /// se re-renderice también, para que el aviso del navegador al recargar o cerrar la pestaña
    /// (ConfirmExternalNavigation, que se calcula en el render de la página) vea la nota.
    /// </summary>
    private static void AlCambiarNotaInterna()
    {
    }

    /// <summary>
    /// P1-E2b: único punto de verdad de «hay cambios» en la página, para
    /// AvisoCambiosSinGuardar: el modal de actualizar documentación, el drawer de redactar
    /// o el de pedir prioridad comparados con cómo se abrieron, o una respuesta o nota
    /// interna a medias en la conversación abierta. La respuesta a medias no cuenta en la
    /// navegación de los filtros de esta misma página, que no la borra.
    /// </summary>
    private bool HayCambiosSinGuardar =>
        HayCambiosActualizarDocumento || HayCambiosRedactar || HayCambiosPrioridad
        || (!_navegandoDentroDeLaPagina && HayRespuestaONotaAMedias);

    // Cada Drawer/Modal lee solo el suyo: cerrarlo no descarta la respuesta a medias.
    // Un vencimiento a medias no cambia el valor del formulario («»), pero es texto que se pierde al cerrar.
    private bool HayCambiosActualizarDocumento =>
        _modalActualizarDocumentoVisible
        && (_instantaneaActualizarDocumento.Difiere(ValoresActualizarDocumento()) || _vencimientoDocumentoNoValido || _emisionDocumentoNoValida);

    private bool HayCambiosRedactar => _redactarVisible && _instantaneaRedactar.Difiere(ValoresRedactar());

    private bool HayCambiosPrioridad =>
        _drawerPrioridadVisible && _borradorPrioridad is not null && _instantaneaPrioridad.Difiere(ValoresPrioridad());

    // El composer solo se pinta con una conversación abierta en la vista «Clientes».
    private bool HayRespuestaONotaAMedias =>
        _detalle is not null && _vista == "clientes"
        && (!string.IsNullOrWhiteSpace(_textoRespuesta) || _adjuntosPendientes.Count > 0
            || !string.IsNullOrWhiteSpace(_emailFallback) || _composerNota?.TieneTextoSinGuardar == true
            || _notaFija?.TieneTextoSinGuardar == true);

    private object?[] ValoresActualizarDocumento() =>
    [
        _tipoDocumentoIdFormulario, _propietarioDocumentoFormulario, _trabajadorDocumentoIdFormulario,
        _empresaDocumentoIdFormulario, _fechaEmisionDocumentoFormulario, _fechaVencimientoDocumentoFormulario,
        _noCaducaDocumentoFormulario, _comentariosDocumentoFormulario,
    ];

    private object?[] ValoresRedactar() => [_conexionRedactarId, _redactarDestinatarios, _redactarAsunto, _redactarCuerpo];

    private object?[] ValoresPrioridad() => [_destinatarioPrioridad, _asuntoPrioridad, _cuerpoPrioridad];

    // FS-15: sin buzón no hay formulario que rellenar; se dice por qué y quién lo conecta (el kit deja «Enviar» deshabilitado con su motivo).
    private RenderFragment SinBuzonRedactar => builder =>
    {
        builder.OpenComponent<AvisoSinBuzonCorreo>(0);
        builder.CloseComponent();
    };

    // El borrador no llegó (la carga falló): el kit pinta el error en lugar de los campos y deja «Enviar» deshabilitado con su motivo.
    private RenderFragment? SinBorradorPrioridad => _borradorPrioridad is null && !_cargandoBorradorPrioridad ? PintarErrorBorradorPrioridad : null;

    private void PintarErrorBorradorPrioridad(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "alerta-formulario");
        builder.AddAttribute(2, "role", "alert");
        builder.AddContent(3, _mensajeErrorPrioridad ?? ErroresTextos["ErrorBorradorPrioridad"].Value);
        builder.CloseElement();
    }

    private void FijarInstantaneaActualizarDocumento() => _instantaneaActualizarDocumento.Fijar(ValoresActualizarDocumento());

    private void CerrarFormulariosDescartando()
    {
        if (_modalActualizarDocumentoVisible) CerrarModalActualizarDocumento();
        _redactarVisible = false;
        if (_drawerPrioridadVisible) CerrarDrawerPrioridad();
        _textoRespuesta = string.Empty;
        _macroSeleccionadaId = string.Empty;
        _adjuntosPendientes.Clear();
        _errorAdjuntos = null;
        _emailFallback = string.Empty;
        _composerNota?.Descartar();
        _notaFija?.Descartar();
    }

    // ---- Nota fija del contacto y notas del equipo (E21) ----

    private NotaFijaContacto? _notaFija;

    private ConversacionDetalleDto? _detalleDelInterlocutor;
    private InterlocutorNotaFija.Interlocutor? _interlocutorNotaFija;

    private InterlocutorNotaFija.Interlocutor? InterlocutorNotaFijaActual
    {
        get
        {
            if (_detalle is null) return null;
            if (!ReferenceEquals(_detalleDelInterlocutor, _detalle))
            {
                // Recarga de la misma conversación (un mensaje nuevo, un envío) con la nota fija en edición o guardándose: el
                // interlocutor se mantiene hasta que termine. Si el último entrante es ahora de otro participante, cambiarlo
                // vaciaría la edición sin aviso; se recalcula en el primer render tras salir de la edición.
                if (_detalleDelInterlocutor?.Id == _detalle.Id && _notaFija?.EnEdicion == true) return _interlocutorNotaFija;

                _detalleDelInterlocutor = _detalle;
                _interlocutorNotaFija = InterlocutorNotaFija.De(_detalle);
            }

            return _interlocutorNotaFija;
        }
    }

    private Task<IReadOnlyDictionary<Guid, string>> ResolverNombresAutoresAsync(IReadOnlyCollection<Guid> ids) =>
        DirectorioUsuarios.ObtenerNombresVisiblesAsync(ids.ToList(), _ciclo.Token);

    /// <summary>
    /// Nota fija del contacto o notas del equipo que enseñar en el panel de contexto (revisión Codex r1 de la #2163, MEDIO 6):
    /// una conversación anclada a una Empresa, sin Cliente empresarial activo ni acciones pendientes, también las tiene.
    /// </summary>
    private bool HayNotasDeContexto =>
        _detalle is not null && (InterlocutorNotaFijaActual is not null || _notasInternas.Count > 0);

    /// <summary>
    /// El detalle en memoria es el de la conversación seleccionada (revisión Codex r3 de la #2163, MEDIO). Al pulsar otra
    /// conversación la selección cambia antes de esperar su detalle, y hasta que llega <c>_detalle</c> sigue siendo el de la
    /// anterior: su panel de contexto no se pinta, para que su nota fija no se edite ni se guarde bajo la selección nueva.
    /// Recargar la misma conversación (tras enviar, por ejemplo) no lo oculta: el Id no cambia.
    /// </summary>
    private bool DetalleEsDeLaSeleccion => _detalle is not null && _detalle.Id == _conversacionSeleccionadaId;

    /// <summary>
    /// El contexto en memoria (ficha, centros, selectores y acciones) es el de este detalle y su Cliente empresarial. Que el detalle
    /// sea el de la selección no basta: <c>CargarDetalleAsync</c> lo asigna antes de esperar el contexto nuevo, y durante esa espera
    /// la ficha y los selectores aún son los del Cliente empresarial anterior (revisión Codex r2 de la #2167, MEDIO 1). Hasta que la
    /// carga fija el par (conversación, Cliente) no se pinta nada del contexto.
    /// </summary>
    private bool ContextoEsDelDetalle => DetalleEsDeLaSeleccion && ContextoEs(_detalle!.Id, ClienteDelDetalle(_detalle));

    /// <summary>
    /// La nota fija de esta conversación se está editando o guardando. Si una recarga cambia el Cliente empresarial, el contexto
    /// deja de ser el del detalle hasta que la carga lo fija; mientras tanto el componente de la nota fija no puede desmontarse, o
    /// su borrador se perdería sin pasar por la confirmación de abandono (revisión Codex r3 de la #2167, MEDIO 1).
    /// </summary>
    private bool NotaFijaEnEdicionDelDetalle => DetalleEsDeLaSeleccion && NotaFijaEnEdicionDeLaConversacion(_detalle!.Id);

    private bool NotaFijaEnEdicionDeLaConversacion(Guid conversacionId) =>
        _detalle?.Id == conversacionId && _detalleDelInterlocutor?.Id == conversacionId && _notaFija?.EnEdicion == true;

    /// <summary>Panel de contexto: el del detalle con su contexto vigente, o solo la nota fija en edición mientras ese contexto se recarga.</summary>
    private bool PanelContextoVisible =>
        ContextoEsDelDetalle
            ? _clienteActivo is not null || HayAccionesPendientes || HayNotasDeContexto
            : NotaFijaEnEdicionDelDetalle;

    // Macros y centros del compositor salen de ese mismo contexto: hasta que es el del detalle no se ofrecen los del hilo anterior.
    private IReadOnlyList<MacroListaDto> MacrosDelContexto => ContextoEsDelDetalle ? _macrosDisponibles : [];
    private IReadOnlyList<CentroSelectorDto> CentrosDelContexto => ContextoEsDelDetalle ? _centrosClienteActivo : [];

    /// <summary>
    /// Centro elegido en «Compartir formatos de centro» mientras siga entre los Centros del contexto vigente; si no, ninguno.
    /// «Pedir prioridad» solo se ofrece, prepara y envía sobre este Centro (revisión Codex r5 de la #2167, MEDIO 3).
    /// </summary>
    private Guid? CentroSeleccionadoVigente =>
        Guid.TryParse(_centroFormatosSeleccionado, out var id) && CentrosDelContexto.Any(c => c.Id == id) ? id : null;

    /// <summary>
    /// Lo que salió en un envío del composer, capturado antes de esperar: la apertura en la que se escribió, el texto, los
    /// adjuntos por identidad, el aviso de adjuntos que acompañaba a ese borrador y, en el fallback, la dirección.
    /// </summary>
    private sealed record BorradorEnviado(
        Vigencia Vigencia, string Texto, IReadOnlyList<AdjuntoParaEnviarDto> Adjuntos, string? ErrorAdjuntos, string? EmailFallback);

    /// <summary>
    /// Tras un envío con éxito se vacía exactamente lo enviado, y solo en la apertura que lo envió (revisión Codex r6 de la #2167,
    /// MEDIO 2): el texto si no ha cambiado, cada adjunto enviado (por referencia: un archivo leído durante el envío es otra
    /// instancia aunque se llame igual), el aviso de adjuntos si sigue siendo el de entonces y la dirección del fallback si sigue
    /// siendo la usada. Lo añadido o cambiado mientras viajaba el envío se queda en el composer.
    /// </summary>
    private void LimpiarLoEnviado(BorradorEnviado enviado)
    {
        if (!MismaApertura(enviado.Vigencia)) return;

        if (_textoRespuesta == enviado.Texto)
        {
            _textoRespuesta = string.Empty;
            _macroSeleccionadaId = string.Empty;
        }
        _adjuntosPendientes.RemoveAll(a => enviado.Adjuntos.Any(e => ReferenceEquals(e, a)));
        if (enviado.ErrorAdjuntos is not null && _errorAdjuntos == enviado.ErrorAdjuntos) _errorAdjuntos = null;
        if (enviado.EmailFallback is not null && _emailFallback == enviado.EmailFallback) _emailFallback = string.Empty;
    }
}
