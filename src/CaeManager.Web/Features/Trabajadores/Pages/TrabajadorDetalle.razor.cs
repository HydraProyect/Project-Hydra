using CaeManager.Domain.Common;
using CaeManager.Application.Common;
using CaeManager.Web.Features.Trabajadores.Recursos;
using CaeManager.Web.Features.Documentos.Recursos;
using CaeManager.Web.Recursos;
using Microsoft.Extensions.Localization;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.DocumentacionBase;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Alertas;
using CaeManager.Application.Asignaciones.Commands.CrearAsignaciones;
using CaeManager.Application.Asignaciones.Queries.ObtenerDocumentosFaltantesParaAsignacion;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Asignaciones.Commands.DarDeBajaAsignaciones;
using CaeManager.Application.Asignaciones.Commands.ReactivarAsignacion;
using CaeManager.Application.Gestiones.Commands.CompletarGestion;
using CaeManager.Application.Gestiones.Commands.CrearGestionesParaTrabajador;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Commands.ResolverDeteccionAusente;
using CaeManager.Application.Trabajadores.Queries.ObtenerDeteccionesPorEmpresa;
using CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Trabajadores;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Features.Documentos.Components;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Trabajadores.Pages;

/// <summary>
/// Trabajador 360 (Parte XVI PROMPT 04) — mismo criterio que Centro 360
/// (<c>CentroDetalle.razor.cs</c>): NO reimplementa edición de Información,
/// Vehículos ni Historial, eso ya vive en <c>TrabajadorWorkspacePanel</c>;
/// esta página compone lo que ese panel no ofrece de un vistazo —
/// cumplimiento agregado, documentación exigida COMPLETA por cada centro
/// (a diferencia de la pestaña "Documentación" del panel, que es plana) y
/// las gestiones pendientes.
///
/// «Gestionar» del menú de acciones (antes «Reclamar faltantes»; regla 8 de LEEME-AUTORIDAD, 2026-10-18):
/// <c>EnviarReclamacionCommand</c> opera a grano Cliente (reclama todo lo pendiente de ese Cliente, no hace
/// falta acotar por Centro) y es un correo a un tercero. Decisión del usuario 2026-08-18, ampliada el
/// 2026-10-18: <see cref="ReclamarFaltantesAsync"/> abre siempre la confirmación (con uno o con varios
/// Clientes), el gestor elige a qué Cliente(s) reclamar y no se envía nada hasta que confirma; nunca se
/// inventa un criterio automático (próxima visita, "el más urgente"...) que el usuario no pidió.
/// Desde 2026-10-18 (decisión de Chris), eso queda solo para un Trabajador de Subcontrata: con un Trabajador de
/// Empresa, «Gestionar» abre <c>DrawerReclamacionLote</c>, que reclama a la Empresa que lo emplea, al contacto de
/// reclamación de su Delegación o, si no hay Delegación con correo, a la agenda de la Empresa; tampoco ahí sale
/// nada sin el clic del gestor.
/// </summary>
public partial class TrabajadorDetalle : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    /// <summary>Rechazo del servidor al crear la gestión: va en el aviso fijo del modal, junto al formulario que hay que corregir (antes, un toast).</summary>
    private string? _errorCrearGestion;

    /// <summary>Todos los envíos de la reclamación fallaron: el modal sigue abierto con la selección y el motivo en su aviso fijo.</summary>
    private string? _errorReclamar;

    internal const string PestanaOperacion = "operacion";
    internal const string PestanaCentros = "centros";
    internal const string PestanaDocumentacion = "documentacion";
    internal const string PestanaFicha = "ficha";
    internal const string PestanaPlataformas = "plataformas";
    internal const string PestanaHistorial = "historial";

    private static readonly string[] PestanasValidas = [PestanaOperacion, PestanaCentros, PestanaDocumentacion, PestanaPlataformas, PestanaFicha, PestanaHistorial];

    [Parameter] public Guid TrabajadorId { get; set; }

    /// <summary>Pestaña activa, en la URL (sin parámetro o con un valor desconocido = Operación), como en Empresa 360.</summary>
    [Parameter, SupplyParameterFromQuery(Name = "pestana")] public string? Pestana { get; set; }

    [Parameter, SupplyParameterFromQuery(Name = "estado")] public string? Estado { get; set; }

    /// <summary>
    /// <c>accion=subir</c> abre la subida con este Trabajador fijado, como el botón «Subir documento» de la cabecera
    /// (enlace «Subir documento para…» del palette). Solo con escritura —la misma comprobación que
    /// <see cref="SoloConEscritura"/> hace con ese botón—; sin ella, el enlace se queda en la ficha y no abre nada. Se
    /// consume una vez y se quita de la URL, para que recargar o volver atrás no reabra el drawer.
    /// </summary>
    [Parameter, SupplyParameterFromQuery(Name = "accion")] public string? Accion { get; set; }

    internal const string AccionSubir = "subir";

    [CascadingParameter] private Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState>? EstadoAutenticacion { get; set; }

    private string? _accionDeLaUrl;
    private bool _subidaDeLaUrlPendiente;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ContextWorkspaceService WorkspaceService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosTrabajadores> Textos { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosComunes> Comunes { get; set; } = default!;

    private bool _editorFichaVisible;
    private bool _corregirDocumentoVisible;

    /// <summary>
    /// El número de afiliación se guarda solo con dígitos; se presenta como en la maqueta
    /// («41 1234567 89»): provincia, número y dígitos de control.
    /// </summary>
    public static string? FormatoAfiliacion(string? digitos) =>
        digitos is { Length: >= 5 } d ? $"{d[..2]} {d[2..^2]} {d[^2..]}" : digitos;
    private string? _estadoCentrosDeLaUrl;
    private IReadOnlySet<string> _estadosCentros = new HashSet<string>();

    private IReadOnlyList<CentroDocumentacionTrabajadorDto> CentrosVisibles =>
        _centros.Where(c => _estadosCentros.Count == 0 || _estadosCentros.Contains(EstadoDocumentoFicha360.Clave(c.PeorEstado)))
            .OrderBy(c => SeveridadEstadoDocumento.Rango(c.PeorEstado)).ThenBy(c => c.CentroNombre, StringComparer.CurrentCulture).ToList();

    private IReadOnlyDictionary<string, int> RecuentosCentros =>
        _centros.GroupBy(c => EstadoDocumentoFicha360.Clave(c.PeorEstado)).ToDictionary(g => g.Key, g => g.Count());

    private IReadOnlyList<OpcionFranjaEstado> OpcionesCentros =>
        new[] { EstadoDocumento.Vencido, EstadoDocumento.Faltante, EstadoDocumento.EnTolerancia, EstadoDocumento.Urgente, EstadoDocumento.SinConfirmar, EstadoDocumento.Vigente }
            .Select(e => new OpcionFranjaEstado(EstadoDocumentoFicha360.Texto(e), EstadoDocumentoFicha360.Tono(e), EstadoDocumentoFicha360.Clave(e))).ToList();

    private void CambiarEstadoCentros(string? valor)
    {
        _estadosCentros = (valor ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        _estadoCentrosDeLaUrl = EstadoDocumentoFicha360.ClavesEnUrl(_estadosCentros);
        NavigationManager.ActualizarFiltroEnUrl("estado", _estadoCentrosDeLaUrl);
    }

    private TrabajadorDetalleDto? _detalle;
    private IReadOnlyList<CentroDocumentacionTrabajadorDto> _centros = [];

    /// <summary>La fila del Trabajador en <see cref="ObtenerTrabajadoresQuery"/>: estado, faltantes y desglose de la cabecera.</summary>
    private TrabajadorListaDto? _estadoTrabajador;
    private bool _cargando = true;
    private bool _error;

    /// <summary>
    /// La consulta devolvió null: la ficha no está en el Tenant activo (o no existe; es el mismo estado, § 4.5).
    /// No es un fallo transitorio, así que no lleva «Reintentar»: lo pinta <c>FichaNoEncontrada</c>.
    /// </summary>
    private bool _noEncontrada;

    private string _pestanaActiva = PestanaOperacion;
    private string? _pestanaDeLaUrl;
    private bool _urlAdoptada;

    /// <summary>
    /// Trabajador que esta instancia tiene cargado. La pestaña viaja en la URL
    /// y llega también por <see cref="OnParametersSetAsync"/>: sin esto, cada
    /// cambio de pestaña relanzaría la carga entera y cerraría el acordeón.
    /// </summary>
    private Guid? _trabajadorCargado;
    private readonly HashSet<Guid> _expandidosCentro = [];

    private DrawerGestionDocumento _drawerGestion = default!;

    private bool _crearGestionVisible;
    private IReadOnlyList<TipoDocumentoListaDto> _tiposDocumentoDisponibles = [];
    private string _tipoDocumentoParaGestion = string.Empty;
    private bool _creandoGestion;

    private bool _reclamarFaltantesVisible;
    private bool _reclamandoFaltantes;
    private IReadOnlyList<ClienteReclamableDto> _clientesReclamables = [];
    private readonly HashSet<Guid> _clientesSeleccionadosReclamar = [];

    private record ClienteReclamableDto(Guid ClienteId, string ClienteRazonSocial, IReadOnlyList<Guid> DocumentoIds);

    /// <summary>
    /// Tope de la lista de documentos del trabajador. Un trabajador real tiene
    /// decenas; si alguna vez pasa del tope, la pestaña lo dice («Mostrando N
    /// de M») en vez de callarlo.
    /// </summary>
    private const int TopeDocumentos = 200;

    /// <summary>
    /// Documentación del trabajador a día de hoy, sin pasar por los Centros
    /// (decisión del propietario 2026-09-24, Q1–Q4 = (a)): alimenta la franja
    /// «Por vencer» de Operación y la pestaña Documentación. Es la misma
    /// ObtenerDocumentosQuery que la pestaña del panel, con su alcance.
    /// </summary>
    private IReadOnlyList<DocumentoListaDto> _documentos = [];
    private int _totalDocumentos;

    /// <summary>
    /// Detección pendiente de tipo Ausente de este Trabajador (no aparece en el último documento de personal de su Empresa):
    /// alimenta el aviso «Podría estar de baja». Null si no hay ninguna o el lector no puede gestionar la Empresa.
    /// </summary>
    private DeteccionTrabajadorDto? _deteccionBaja;
    private bool _confirmarBajaDeteccionVisible;
    private bool _resolviendoDeteccion;
    private bool _cargandoDocumentos = true;
    private bool _errorDocumentos;

    private IReadOnlyList<GestionListaDto> _gestionesPendientes = [];
    private bool _cargandoGestiones = true;
    private readonly HashSet<Guid> _completandoGestion = [];
    private readonly HashSet<Guid> _dandoDeBajaAsignacion = [];
    private bool _confirmarBajaAsignacionVisible;
    private Guid? _asignacionABajar;
    private string _centroABajar = string.Empty;
    private readonly HashSet<Guid> _reactivandoAsignacion = [];

    /// <summary>Trabajador cuya ficha se está pintando: al cambiar, lo que quedara preparado en una modal deja de valer.</summary>
    private Guid _trabajadorEnPantalla;

    /// <summary>
    /// Se cancela al retirarse la página: las consultas en curso dejan de
    /// trabajar para nadie y ninguna respuesta tardía repinta un componente
    /// ya desechado. Mismo patrón que <c>Empresas.razor.cs</c>.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    /// <summary>
    /// Número de la última carga. Cada carga captura el suyo ANTES del
    /// <c>await</c> y, al volver, solo escribe estado si sigue siendo la
    /// vigente. Sin esto, navegar de un trabajador a otro —o recargar tras
    /// guardar un documento— dejaba que la respuesta de la pregunta anterior
    /// pintara la cabecera, el anillo y los centros del trabajador que ya no
    /// se está mirando. La carga de gestiones comparte el mismo número: una
    /// respuesta de antes de recargar ya no pertenece a lo que se ve.
    /// </summary>
    private int _cargaVigente;

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

    private string? NombreCompleto => _detalle is null ? null : $"{_detalle.Nombre} {_detalle.Apellidos}";

    /// <summary>
    /// Rótulo del documento de identidad en la cabecera (maqueta del 20-10: «DNI», «NIE» o «Pasaporte», según el valor
    /// guardado, no un «DNI» fijo). Un valor heredado que no es un documento válido (marcadores como «SIN DNI») se rotula
    /// «DNI», igual que <see cref="CriterioExtranjeroCobrable"/> no lo cuenta como Pasaporte.
    /// </summary>
    internal string RotuloDocumentoIdentidad => TipoRotuladoDocumento switch
    {
        TipoDocumentoTrabajador.Nie => Textos["FichaRotuloNie"],
        TipoDocumentoTrabajador.Pasaporte => Textos["FichaRotuloPasaporte"],
        _ => Textos["FichaRotuloDni"],
    };

    /// <summary>Lo que dice el botón de copiar junto al rótulo («el NIE»…): nunca «el DNI» junto a un NIE.</summary>
    internal string EtiquetaCopiarDocumentoIdentidad => TipoRotuladoDocumento switch
    {
        TipoDocumentoTrabajador.Nie => Textos["FichaCopiarNie"],
        TipoDocumentoTrabajador.Pasaporte => Textos["FichaCopiarPasaporte"],
        _ => Textos["FichaCopiarDni"],
    };

    private TipoDocumentoTrabajador TipoRotuladoDocumento
    {
        get
        {
            var dni = _detalle?.Dni;
            return CriterioDocumentoIdentidadTrabajador.TipoGuardado(dni) switch
            {
                TipoDocumentoTrabajador.Nie => TipoDocumentoTrabajador.Nie,
                TipoDocumentoTrabajador.Pasaporte when CriterioDocumentoIdentidadTrabajador.Clasificar(dni).EsValido => TipoDocumentoTrabajador.Pasaporte,
                _ => TipoDocumentoTrabajador.Dni,
            };
        }
    }

    /// <summary>
    /// Página 360 del empleador, para el icono 360 junto a su nombre: Empresa 360 o Subcontrata 360.
    /// El nombre abre la consulta rápida (<see cref="AbrirEmpleador"/>); el icono, la página.
    /// </summary>
    private string? PaginaEmpleador => _detalle switch
    {
        { EmpresaId: { } empresaId } => $"/empresas/{empresaId}",
        { SubcontrataId: { } subcontrataId } => $"/subcontratas/{subcontrataId}",
        _ => null,
    };

    /// <summary>
    /// Las pestañas de esta ficha. «Contactos» ya no es pestaña: sus datos
    /// viven en el lateral (decisión del propietario 2026-09-24). No es estático porque el recuento de
    /// «Operación» depende de los datos cargados: el mockup Gen 2 pinta ahí
    /// la píldora con los documentos que hoy tienen incidencia.
    /// </summary>
    private IReadOnlyList<PestanaDefinicion> Pestanas =>
    [
        new(PestanaOperacion, Textos["PestanaOperacion"])
        {
            Contador = TotalConIncidencia == 0
                ? null
                : new ContadorPestana(
                    TotalConIncidencia,
                    TotalConIncidencia == 1 ? Textos["ContadorIncidenciasUno"] : Textos["ContadorIncidenciasVarios"],
                    EnAlerta: true)
        },
        // Maqueta 17-10: «Centros» lleva el recuento de Centros asignados, en neutro (no es una alerta).
        new(PestanaCentros, Textos["PestanaCentros"])
        {
            Contador = _centros.Count == 0
                ? null
                : new ContadorPestana(
                    _centros.Count,
                    _centros.Count == 1 ? Textos["ContadorCentrosUno"] : Textos["ContadorCentrosVarios"])
        },
        // Maqueta 17-10: «Documentación» lleva el recuento de documentos que se listan, en neutro.
        new(PestanaDocumentacion, Textos["PestanaDocumentacion"])
        {
            Contador = Grupos.Total == 0
                ? null
                : new ContadorPestana(
                    Grupos.Total,
                    Grupos.Total == 1 ? Textos["ContadorDocumentosUno"] : Textos["ContadorDocumentosVarios"])
        },
        new(PestanaPlataformas, Textos["PestanaPlataformas"])
        {
            Contador = TotalIncidenciasPlataformas == 0 ? null : new ContadorPestana(
                TotalIncidenciasPlataformas, Textos["PlataformasSoloIncidencias", TotalIncidenciasPlataformas], EnAlerta: true)
        },
        new(PestanaFicha, Comunes["PestanaFicha"]),
        new(PestanaHistorial, Textos["PestanaHistorial"])
    ];

    private int TotalIncidenciasPlataformas => _centros.Sum(c => c.Documentos.Sum(d =>
        d.AcreditacionesEnElCentro?.Count(a => a.Estado is EstadoAcreditacion.PendienteDeSubir or EstadoAcreditacion.Subida or EstadoAcreditacion.Rechazada) ?? 0));

    // D-22: «Sin confirmar» no es incidencia, cuenta como al día con aviso (misma regla que el panel
    // Documentación base, #1015: DocumentacionBaseTrabajador.CuentaComoIncidencia).
    private static bool EsIncidencia(DocumentoRequeridoDto documento) =>
        DocumentacionBaseTrabajador.CuentaComoIncidencia(documento.Estado);

    private static int Incidencias(CentroDocumentacionTrabajadorDto centro) => centro.Documentos.Count(EsIncidencia);

    private static bool TieneIncidencias(CentroDocumentacionTrabajadorDto centro) => centro.Documentos.Any(EsIncidencia);

    private static int SinConfirmar(CentroDocumentacionTrabajadorDto centro) =>
        centro.Documentos.Count(d => d.Estado == EstadoDocumento.SinConfirmar);

    private int TotalConIncidencia => _centros.Sum(Incidencias);

    /// <summary>
    /// Documentos del Trabajador sin vigencia confirmada, cada uno una vez: del desglose de <see cref="_estadoTrabajador"/>,
    /// no sumando Centros (un mismo documento exigido en dos Centros se contaba dos veces).
    /// </summary>
    private int TotalSinConfirmar => _estadoTrabajador?.Incidencias.Count(i => i.Estado == EstadoDocumento.SinConfirmar) ?? 0;

    private IReadOnlyList<BreadcrumbElemento> Miguero =>
        new[] { new BreadcrumbElemento(Textos["MigaTrabajadores"]), new BreadcrumbElemento(NombreCompleto ?? "…") };

    /// <summary>
    /// % del Trabajador (contexto <see cref="ContextoCumplimiento.Trabajador"/>): la suma de las fracciones de cada Centro,
    /// que ya mide <see cref="CumplimientoDocumental"/> (el listado de documentos no incluye «Sin caducidad», así que no
    /// sirve para contar). No tiene fórmula propia.
    /// </summary>
    private int? Cumplimiento => FraccionCumplimiento.Sumar(_centros.Select(c => c.Cumplimiento)).Porcentaje;

    private CentroDocumentacionTrabajadorDto? CentroMasUrgente =>
        _centros.Count == 0 ? null : _centros.MinBy(c => SeveridadEstadoDocumento.Rango(c.PeorEstado));

    /// <summary>
    /// El estado documental del Trabajador en la cabecera: el MISMO que su fila del listado de Trabajadores y de Empresa 360
    /// (<see cref="ObtenerTrabajadoresQuery"/>), no el peor de los Centros. Solo va en rojo por un Tipo que un Centro visible marca
    /// «bloquea el acceso» y que falta o está vencido allí (Vencido &gt; Pendiente); un vencido sin esa marca, o dentro de la
    /// tolerancia del Centro, sale como aviso en ámbar (<c>VencidoSinBloqueo</c>); después Por vencer &gt; Sin incidencias.
    /// </summary>
    private EstadoDocumento? PeorEstadoGlobal => _estadoTrabajador?.EstadoDocumental;

    /// <summary>Los Tipos que algún Centro visible le marca obligatorios («bloquea el acceso») y de los que no tiene documento, sin repetir: los del mismo cálculo que el estado.</summary>
    private IReadOnlyList<string> TiposFaltantes => _estadoTrabajador?.DocumentosFaltantes ?? [];

    /// <summary>El primer documento faltante/vencido del centro más urgente — lo que abre el botón primario "Subir documento" de la cabecera (mockup § cabecera: el botón y el badge de urgencia hablan del mismo problema).</summary>
    private DocumentoRequeridoDto? DocumentoMasUrgente =>
        CentroMasUrgente?.Documentos.FirstOrDefault(EsIncidencia);

    /// <summary>
    /// Solo recarga cuando cambia el Trabajador. La pestaña también llega por
    /// aquí (viaja en la URL) y no pide nada: se adopta y se pinta.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        AdoptarUrl();

        if (_trabajadorCargado == TrabajadorId)
            return;

        _trabajadorCargado = TrabajadorId;
        await CargarAsync();
    }

    /// <summary>
    /// Adopta la pestaña cuando cambia en la URL AHÍ FUERA —un enlace, el
    /// botón «atrás», otro Trabajador—; lo que cambia la propia página ya lo
    /// apuntó antes de navegar.
    /// </summary>
    private void AdoptarUrl()
    {
        if (!string.Equals(_accionDeLaUrl, Accion, StringComparison.Ordinal))
        {
            _accionDeLaUrl = Accion;
            _subidaDeLaUrlPendiente = string.Equals(Accion, AccionSubir, StringComparison.Ordinal);
        }
        if (!string.Equals(_estadoCentrosDeLaUrl, Estado, StringComparison.Ordinal))
        {
            _estadoCentrosDeLaUrl = Estado;
            _estadosCentros = EstadoDocumentoFicha360.ClavesDesdeUrl(Estado);
        }
        if (_urlAdoptada && string.Equals(_pestanaDeLaUrl, Pestana, StringComparison.Ordinal))
            return;

        _urlAdoptada = true;
        _pestanaDeLaUrl = Pestana;
        _pestanaActiva = PestanasValidas.Contains(Pestana) ? Pestana! : PestanaOperacion;
    }

    /// <summary>Cambia de pestaña y la deja en la URL sin recargar la ficha; Operación, la de entrada, no lleva parámetro.</summary>
    private void CambiarPestana(string pestana)
    {
        _pestanaActiva = pestana;
        _pestanaDeLaUrl = pestana == PestanaOperacion ? null : pestana;
        NavigationManager.ActualizarFiltroEnUrl("pestana", _pestanaDeLaUrl);
    }

    /// <summary>
    /// Abre la subida pedida por <c>accion=subir</c> cuando la ficha ya está cargada y el drawer montado (solo en la
    /// fase interactiva: OnAfterRender no corre en el prerenderizado).
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_subidaDeLaUrlPendiente || _cargando || _detalle is null || _drawerGestion is null)
            return;

        _subidaDeLaUrlPendiente = false;
        _accionDeLaUrl = null;
        NavigationManager.ActualizarFiltroEnUrl("accion", null);

        if (!await SoloConEscritura.PuedeEscribirAsync(EstadoAutenticacion))
            return;

        // Alta de un documento nuevo con el Trabajador fijado: el enlace dice «Subir documento para…», no
        // «gestionar el más urgente» (eso es el botón de la cabecera).
        await _drawerGestion.AbrirCrearParaTrabajadorAsync(TrabajadorId);
    }

    private async Task CargarAsync()
    {
        if (_desechado)
            return;

        // Todo lo que define la pregunta se lee ANTES del await.
        var carga = ++_cargaVigente;
        var trabajadorId = TrabajadorId;

        // Las modales se pintan fuera del bloque de la página, así que
        // sobrevivían al cambio de ruta: abrir «Gestionar» (reclamar) para A, navegar a
        // B y confirmar reclamaba a B. Cambiar de trabajador las cierra y
        // tira lo que habían preparado.
        if (_trabajadorEnPantalla != trabajadorId)
        {
            _trabajadorEnPantalla = trabajadorId;
            CerrarModalesPendientes();

            // Los documentos llegan en una carga aparte, después del detalle
            // y los centros: sin vaciarlos aquí, la franja «Por vencer» de B
            // enseñaría los de A mientras tanto.
            _documentos = [];
            _totalDocumentos = 0;
            _deteccionBaja = null;
            _cargandoDocumentos = true;
            _errorDocumentos = false;
            // Igual con el estado de la cabecera: el de A no puede quedarse bajo los Centros de B.
            _estadoTrabajador = null;
        }

        _cargando = true;
        _error = false;
        _noEncontrada = false;

        try
        {
            var detalle = await Mediator.Send(new ObtenerTrabajadorPorIdQuery(trabajadorId), _ciclo.Token);
            if (!EsVigente(carga))
                return;

            _detalle = detalle;
            if (_detalle is null)
            {
                _noEncontrada = true;
                return;
            }

            if (_pestanaDeLaUrl == PestanaFicha)
                _pestanaActiva = PestanaFicha;

            // El acordeón se cierra al cambiar de pregunta: los Ids
            // expandidos eran de la respuesta anterior y no significan nada
            // en la nueva.
            _expandidosCentro.Clear();

            var centros = await Mediator.Send(new ObtenerDocumentacionPorCentroDeTrabajadorQuery(trabajadorId), _ciclo.Token);
            if (!EsVigente(carga))
                return;

            _centros = centros;

            // El estado de la cabecera sale del cálculo único del listado (su fila, con faltantes y desglose).
            var fila = await Mediator.Send(new ObtenerTrabajadoresQuery(
                null, Pagina: 1, TamanoPagina: 1, ConDesgloseDocumental: true, TrabajadorId: trabajadorId), _ciclo.Token);
            if (!EsVigente(carga))
                return;

            _estadoTrabajador = fila.Elementos.FirstOrDefault();

            // RendererInfo.IsInteractive: OnParametersSetAsync (y por tanto
            // CargarAsync) también corre durante el prerenderizado estático
            // de una carga en frío — un "_ = " sin await ahí deja la tarea
            // de gestiones en vuelo cuando ASP.NET Core ya dio por completada
            // esa fase y libera el scope de DI, tirando el DbContext a mitad
            // de consulta (reproducido en vivo: "Connection is not open",
            // DbContext ya liberado, ObjectDisposedException en el semáforo
            // de PuertaAccesoDatos — todo en cascada desde este único origen,
            // solo visible en una carga en frío real como un deep-link con
            // "ctx", nunca navegando ya con el circuito interactivo vivo).
            // Esperar a la fase interactiva evita la carrera sin perder la
            // carga en paralelo: sigue sin bloquear el resto de la página.
            if (RendererInfo.IsInteractive)
            {
                _ = CargarGestionesAsync();
                _ = CargarDocumentosAsync();
                _ = CargarDeteccionBajaAsync();
            }
        }
        catch (Exception) when (!EsVigente(carga))
        {
            // Una carga superada que falla no es un error de la vigente: no
            // puede tapar su resultado con el estado de error.
        }
        catch (Exception)
        {
            _error = true;
        }
        finally
        {
            if (EsVigente(carga))
            {
                _cargando = false;
                // Sin ficha no hay subida que abrir: la acción del enlace no queda esperando a una recarga.
                if (_error)
                    _subidaDeLaUrlPendiente = false;
                StateHasChanged();
            }
        }
    }

    private async Task CargarGestionesAsync()
    {
        if (_desechado)
            return;

        // La respuesta solo vale para la carga que la pidió: tras cambiar de
        // trabajador (o recargar), unas gestiones de antes ya no son de este
        // trabajador ni de este estado.
        var carga = _cargaVigente;
        var trabajadorId = TrabajadorId;

        _cargandoGestiones = true;
        try
        {
            var resultado = await Mediator.Send(new ObtenerGestionesQuery(
                Busqueda: null, Estado: EstadoGestion.Pendiente, TrabajadorId: trabajadorId, TamanoPagina: 50), _ciclo.Token);
            if (!EsVigente(carga))
                return;

            _gestionesPendientes = resultado.Elementos;
        }
        catch (Exception) when (!EsVigente(carga))
        {
            return;
        }
        catch (Exception)
        {
            _gestionesPendientes = [];
        }
        finally
        {
            if (EsVigente(carga))
            {
                _cargandoGestiones = false;
                StateHasChanged();
            }
        }
    }

    /// <summary>
    /// Carga los documentos del trabajador y los tipos generales. Mismo
    /// patrón que <see cref="CargarGestionesAsync"/>: en paralelo con el resto
    /// y solo para la carga que la pidió. Un fallo aquí no tumba la página:
    /// se queda en la pestaña Documentación, con su reintento.
    /// </summary>
    private async Task CargarDocumentosAsync()
    {
        if (_desechado)
            return;

        var carga = _cargaVigente;
        var trabajadorId = TrabajadorId;

        _cargandoDocumentos = true;
        _errorDocumentos = false;
        try
        {
            // Orden por Estado ascendente, resuelto en la base de datos: si hay
            // más documentos que el tope, lo que se queda fuera es lo vigente o
            // lo que no caduca, nunca lo que alimenta la franja «Por vencer».
            var documentos = await Mediator.Send(new ObtenerDocumentosQuery(
                trabajadorId, AmbitoAplicacion.Trabajador, Busqueda: null, TamanoPagina: TopeDocumentos,
                OrdenarPor: nameof(DocumentoListaDto.Estado)), _ciclo.Token);
            if (!EsVigente(carga))
                return;

            _documentos = documentos.Elementos;
            _totalDocumentos = documentos.TotalElementos;
        }
        catch (Exception) when (!EsVigente(carga))
        {
            return;
        }
        catch (Exception)
        {
            _documentos = [];
            _totalDocumentos = 0;
            _errorDocumentos = true;
        }
        finally
        {
            if (EsVigente(carga))
            {
                _cargandoDocumentos = false;
                StateHasChanged();
            }
        }
    }

    /// <summary>
    /// Franja «Por vencer» de Operación (Q3 = (a)): los estados Próximo y
    /// Urgente de CalculadoraEstadoDocumento, que ya aplican el umbral ámbar
    /// del tenant. Lo vencido no entra: ya lo señalan los Centros.
    /// </summary>
    private IReadOnlyList<DocumentoListaDto> DocumentosPorVencer =>
        _documentos.Where(d => d.Estado is EstadoDocumento.Proximo or EstadoDocumento.Urgente)
            .OrderBy(d => d.FechaVencimiento ?? DateOnly.MaxValue)
            .ThenBy(d => d.TipoDocumentoNombre, StringComparer.CurrentCulture)
            .ToList();

    /// <summary>Lo peor primero y, a igualdad de estado, por nombre.</summary>
    private IReadOnlyList<DocumentoListaDto> DocumentosOrdenados =>
        _documentos.OrderBy(d => SeveridadEstadoDocumento.Rango(d.Estado))
            .ThenBy(d => d.TipoDocumentoNombre, StringComparer.CurrentCulture)
            .ToList();

    /// <summary>
    /// Una fila de la pestaña Documentación: un Tipo de documento, su peor estado entre los Centros que lo exigen y dónde se
    /// exige. <see cref="Requerido"/> es el documento exigido (abre la serie de «Gestionar»); sin él, la fila es un documento
    /// del trabajador que ningún Centro exige y se gestiona por su <see cref="DocumentoId"/>.
    /// </summary>
    private sealed record FilaDocumentacion(
        Guid TipoId, string Nombre, EstadoDocumento Estado, DateOnly? Vence, DateOnly? EnToleranciaHasta, string Alcance,
        DocumentoRequeridoDto? Requerido, Guid? DocumentoId)
    {
        /// <summary>Identidad de la fila dentro de su grupo: el Documento si existe y, si falta, el Tipo (único por grupo).</summary>
        public Guid FilaId => DocumentoId ?? TipoId;
    }

    private sealed record GruposDocumentacion(
        IReadOnlyList<FilaDocumentacion> EnTodos, IReadOnlyList<FilaDocumentacion> EnAlgunos, IReadOnlyList<FilaDocumentacion> Otros)
    {
        public int Total => EnTodos.Count + EnAlgunos.Count + Otros.Count;
    }

    /// <summary>
    /// La documentación del trabajador como la agrupa la maqueta Trabajador 360 (2026-10-17): lo exigido en todos sus Centros y lo
    /// exigido solo en algunos (por Tipo, con el peor estado entre los Centros: el documento es el mismo, porque Documento no tiene
    /// CentroId). Lo que el trabajador tiene y ningún Centro exige va aparte, para no esconderlo. Cada grupo, del peor estado al mejor.
    /// </summary>
    private GruposDocumentacion Grupos
    {
        get
        {
            var totalCentros = _centros.Count;
            var enTodos = new List<FilaDocumentacion>();
            var enAlgunos = new List<FilaDocumentacion>();
            var exigidos = new HashSet<Guid>();

            foreach (var porTipo in _centros
                .SelectMany(c => c.Documentos.Select(d => (Centro: c, Documento: d)))
                .GroupBy(x => x.Documento.TipoDocumentoId))
            {
                exigidos.Add(porTipo.Key);
                var centros = porTipo.Select(x => x.Centro.CentroNombre).Distinct().Order(StringComparer.CurrentCulture).ToList();
                var peor = porTipo.Select(x => x.Documento).MinBy(d => SeveridadEstadoDocumento.Rango(d.Estado))!;
                var enTodosLosCentros = porTipo.Select(x => x.Centro.CentroId).Distinct().Count() == totalCentros;
                var fila = new FilaDocumentacion(
                    porTipo.Key, peor.TipoDocumentoNombre, peor.Estado, peor.FechaVencimiento, peor.EnToleranciaHasta,
                    enTodosLosCentros ? AlcanceEnTodos(totalCentros) : AlcanceEnAlgunos(centros), peor, peor.DocumentoId);
                (enTodosLosCentros ? enTodos : enAlgunos).Add(fila);
            }

            var otros = _documentos
                .Where(d => !exigidos.Contains(d.TipoDocumentoId))
                .Select(d => new FilaDocumentacion(
                    d.TipoDocumentoId, d.TipoDocumentoNombre, d.Estado, d.FechaVencimiento, null, Textos["AlcanceNingunCentro"], null, d.Id))
                .ToList();

            return new GruposDocumentacion(Ordenadas(enTodos), Ordenadas(enAlgunos), Ordenadas(otros));
        }
    }

    private static IReadOnlyList<FilaDocumentacion> Ordenadas(IEnumerable<FilaDocumentacion> filas) =>
        filas.OrderBy(f => SeveridadEstadoDocumento.Rango(f.Estado)).ThenBy(f => f.Nombre, StringComparer.CurrentCulture).ToList();

    private string AlcanceEnTodos(int totalCentros) =>
        totalCentros == 1 ? Textos["AlcanceUnicoCentro"] : Textos["AlcanceTodosCentros", totalCentros];

    private string AlcanceEnAlgunos(IReadOnlyList<string> centros) => centros.Count switch
    {
        1 => Textos["AlcanceSoloEn", centros[0]],
        2 => Textos["AlcanceEnDos", centros[0], centros[1]],
        _ => Textos["AlcanceEnVarios", centros[0], centros[1], centros.Count - 2],
    };

    private Task GestionarFilaDocumentacionAsync(FilaDocumentacion fila) =>
        fila.Requerido is { } requerido ? GestionarDocumentoAsync(requerido) : EditarDocumentoAsync(fila.DocumentoId!.Value);

    /// <summary>Documentos del trabajador con la vigencia sin confirmar: Operación los nombra aunque no sean de la documentación base.</summary>
    private IReadOnlyList<DocumentoListaDto> DocumentosSinConfirmar =>
        _documentos.Where(d => d.Estado == EstadoDocumento.SinConfirmar)
            .OrderBy(d => d.TipoDocumentoNombre, StringComparer.CurrentCulture)
            .ToList();

    /// <summary>
    /// Detección «Ausente» pendiente de este Trabajador: el último documento de personal de su Empresa no lo incluye. Solo la lee
    /// quien gestiona la Empresa (la consulta aplica el alcance de gestión); sin permiso o sin detección, el aviso no se pinta.
    /// </summary>
    private async Task CargarDeteccionBajaAsync()
    {
        if (_desechado || _detalle?.EmpresaId is not { } empresaId)
            return;

        var carga = _cargaVigente;
        var trabajadorId = TrabajadorId;
        try
        {
            var resultado = await Mediator.Send(new ObtenerDeteccionesPorEmpresaQuery(empresaId), _ciclo.Token);
            if (!EsVigente(carga) || resultado.EsFallido)
                return;

            _deteccionBaja = resultado.Valor.FirstOrDefault(d => d.Tipo == TipoDeteccion.Ausente && d.TrabajadorExistenteId == trabajadorId);
        }
        catch (Exception)
        {
            // Es un aviso de ayuda: si falla, la ficha sigue sin él.
            return;
        }

        if (EsVigente(carga))
            await InvokeAsync(StateHasChanged);
    }

    private async Task ResolverDeteccionBajaAsync(bool darDeBaja)
    {
        if (_deteccionBaja is not { } deteccion || _resolviendoDeteccion)
            return;

        _resolviendoDeteccion = true;
        try
        {
            var resultado = await Mediator.Send(new ResolverDeteccionAusenteCommand(deteccion.Id, darDeBaja), _ciclo.Token);
            if (resultado.EsFallido)
            {
                // La detección ya no está pendiente (la resolvió otra persona): se cierra el diálogo y se relee el aviso.
                ToastService.MostrarError(resultado.Error);
                _confirmarBajaDeteccionVisible = false;
                _deteccionBaja = null;
                await CargarDeteccionBajaAsync();
                return;
            }

            _confirmarBajaDeteccionVisible = false;
            _deteccionBaja = null;
            if (darDeBaja)
            {
                // Lo que la operación hizo de verdad, no lo que se pidió: otra persona pudo dar ya de baja al Trabajador.
                var toast = resultado.Valor == ResultadoResolucionAusente.DadoDeBaja
                    ? Textos["ToastBajaDeteccionConfirmada"]
                    : Textos["ToastBajaDeteccionYaNoActivo"];
                ToastService.Mostrar(toast, resultado.Valor == ResultadoResolucionAusente.DadoDeBaja ? TonoToast.Exito : TonoToast.Info);
                // La baja es un borrado lógico: la ficha deja de existir, así que se vuelve a la lista.
                NavigationManager.NavigateTo("/trabajadores");
            }
            else
            {
                ToastService.Mostrar(Textos["ToastBajaDeteccionIgnorada"], TonoToast.Info);
            }
        }
        finally
        {
            _resolviendoDeteccion = false;
        }
    }

    // Sin fecha son dos cosas distintas, y una tercera si no hay vigencia que rotular: la regla es de
    // EstadoDocumentoUi.TextoSinFechaDeVencimiento («Sin caducidad» / «Sin confirmar» / «—»), la misma que Centro 360,
    // Subcontrata 360 y Reportes.
    private static string TextoVence(DateOnly? fechaVencimiento, EstadoDocumento estado) =>
        fechaVencimiento is { } vence ? vence.ToString("dd/MM/yyyy")
        : EstadoDocumentoUi.TextoSinFechaDeVencimiento(estado);

    private void IrABreadcrumb(int indice)
    {
        if (indice == 0)
            NavigationManager.NavigateTo("/trabajadores");
    }

    // El tinte de estas filas no cambia el vocabulario ni las acciones de los estados.
    private static TonoFila? TonoDocumentoEnCentro(EstadoDocumento estado) => EstadoDocumentoFicha360.TonoFila(estado);
    private void AlternarExpansionCentro(Guid centroId)
    {
        if (!_expandidosCentro.Remove(centroId))
            _expandidosCentro.Add(centroId);
    }

    private void AbrirInformacion() =>
        WorkspaceService.AbrirAsync(EntidadWorkspace.Trabajador, TrabajadorId, NombreCompleto ?? string.Empty, "informacion");

    private void AbrirEmpleador()
    {
        if (_detalle is null) return;

        if (_detalle.EmpresaId is { } empresaId)
            WorkspaceService.AbrirAsync(EntidadWorkspace.Empresa, empresaId, _detalle.EmpleadorNombre, "informacion");
        else if (_detalle.SubcontrataId is { } subcontrataId)
            WorkspaceService.AbrirAsync(EntidadWorkspace.Subcontrata, subcontrataId, _detalle.EmpleadorNombre, "informacion");
    }

    private Task GestionarDocumentoAsync(DocumentoRequeridoDto documento) =>
        _drawerGestion.AbrirSerieAsync(SerieDeDocumentos, PasoDe(documento));

    private Task EditarDocumentoAsync(Guid documentoId) => _drawerGestion.AbrirEditarAsync(documentoId);

    /// <summary>
    /// «Gestionar» de un indicador de Documentación base que falta: no hay Documento que renovar ni Tipo de documento
    /// concreto, así que abre el mismo Nuevo documento con el Trabajador ya elegido que usa «Subir documento» sin incidencia.
    /// </summary>
    private Task SubirFaltanteAsync() => _drawerGestion.AbrirCrearParaTrabajadorAsync(TrabajadorId);

    private PasoSerieDocumento PasoDe(DocumentoRequeridoDto documento) =>
        PasoSerieDocumento.DeFilaDeTrabajador(documento.DocumentoId, TrabajadorId, documento.TipoDocumentoId);

    /// <summary>
    /// «Guardar y siguiente»: los documentos que piden «Renovar» o «Subir», en el orden en que la pestaña
    /// Documentación los pinta (Centro a Centro, y dentro de cada uno como llegan). Un mismo Documento exigido por
    /// dos Centros cuenta una vez.
    /// </summary>
    private IReadOnlyList<PasoSerieDocumento> SerieDeDocumentos =>
        _centros.SelectMany(c => c.Documentos).Where(d => PasoSerieDocumento.EsDeSerie(d.Estado)).Select(PasoDe).ToList();

    /// <summary>
    /// «Subir documento» de la cabecera y del estado vacío de la pestaña Documentación. Con una
    /// incidencia abre el documento más urgente (el badge y el botón hablan del mismo problema);
    /// sin ella (trabajador recién creado, o todo al día) abre el mismo drawer de Nuevo documento con
    /// el trabajador ya elegido y el tipo por elegir allí (D-06/D-22): acreditar no puede quedarse
    /// sin salida desde la ficha.
    /// </summary>
    private Task SubirDocumentoAsync() =>
        DocumentoMasUrgente is { } documento
            ? GestionarDocumentoAsync(documento)
            : _drawerGestion.AbrirCrearParaTrabajadorAsync(TrabajadorId);

    private async Task ManejarDocumentoGuardadoAsync() => await CargarAsync();

    /// <summary>
    /// FS-13 (auditoría UX de flujos sin salida, 2026-09-24): la baja salía al
    /// primer clic y sin vuelta atrás; la única corrección era un alta nueva,
    /// que partía la historia en dos filas. Ahora se confirma y el aviso
    /// ofrece «Deshacer».
    /// </summary>
    private void AbrirBajaAsignacionConfirm(Guid asignacionId, string centroNombre)
    {
        _asignacionABajar = asignacionId;
        _centroABajar = centroNombre;
        _confirmarBajaAsignacionVisible = true;
    }

    private async Task ConfirmarBajaAsignacionAsync()
    {
        if (_asignacionABajar is not { } asignacionId) return;
        await DarDeBajaAsignacionAsync(asignacionId);

        // Si mientras tanto se abrió otro diálogo (otro trabajador, otra fila),
        // es del usuario: solo se cierra el que confirmó esta baja.
        if (_asignacionABajar != asignacionId) return;
        _confirmarBajaAsignacionVisible = false;
        _asignacionABajar = null;
    }

    private async Task DarDeBajaAsignacionAsync(Guid asignacionId)
    {
        // Guarda de reentrada por asignación, como en CompletarGestionAsync:
        // dos eventos seguidos mandaban dos bajas.
        if (!_dandoDeBajaAsignacion.Add(asignacionId)) return;

        try
        {
            var resultado = await Mediator.Send(new DarDeBajaAsignacionesCommand([asignacionId], DiaDeNegocio.Hoy()));
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            // El comando devuelve éxito aunque no haya dado de baja ninguna
            // (asignación ya cerrada, o fuera de alcance): decir «hecho»
            // entonces es mentir sobre lo que pasó.
            var (dadasDeBaja, errores) = (resultado.Valor.DadasDeBaja, resultado.Valor.Errores);
            if (dadasDeBaja == 0)
            {
                ToastService.Mostrar(
                    errores.Count > 0 ? errores[0] : Textos["ErrorDarDeBaja"].Value,
                    TonoToast.Error);
            }
            else if (errores.Count > 0)
                ToastService.Mostrar(Textos["ToastBajaConAvisos", string.Join("; ", errores)], TonoToast.Advertencia,
                    Textos["ToastAccionDeshacer"], () => DeshacerBajaAsignacionAsync(asignacionId));
            else
                ToastService.Mostrar(Textos["ToastBaja"], TonoToast.Exito,
                    Textos["ToastAccionDeshacer"], () => DeshacerBajaAsignacionAsync(asignacionId));

            await CargarAsync();
        }
        finally
        {
            _dandoDeBajaAsignacion.Remove(asignacionId);
        }
    }

    /// <summary>
    /// Solo cuentan documentos que existen (<c>DocumentoId != null</c>), no vigentes y reclamables según
    /// <see cref="VentanaReclamacion"/> (con FechaVencimiento y dentro de la ventana): un Faltante no tiene fila de
    /// Documento que reclamar, y un «Sin confirmar» sin fecha no tiene vencimiento que renovar. «Gestionar» reclama una
    /// renovación; lo que falta y lo «Sin confirmar» sin fecha se piden con «Gestionar» (Centro 360 y Mi trabajo), que
    /// viaja por las Pendientes del mismo envío. No replica el resto de filtros del envío (asignación activa,
    /// alcance, Centro con gestión CAE): el envío los revalida.
    /// </summary>
    private async Task ReclamarFaltantesAsync()
    {
        // Trabajador de Empresa: sus documentos se reclaman a la Empresa que lo emplea, a la Delegación del Trabajador
        // (decisión de Chris, 2026-10-18), con el mismo cajón que «Gestionar»: vista previa y envío solo con clic explícito.
        // El de Subcontrata sigue reclamándose al Cliente del Centro con la confirmación de abajo.
        if (_detalle?.EmpresaId is not null)
        {
            _reclamacionEmpresaVisible = true;
            return;
        }

        // Solo se ofrece lo que el envío acepta (VentanaReclamacion: con FechaVencimiento y dentro de la
        // ventana). Un «Sin confirmar» sin fecha no tiene vencimiento que renovar: este botón no lo incluye
        // (se pide con «Gestionar» en Centro 360) y, si es lo único pendiente, se explica por qué.
        var hoy = DiaDeNegocio.Hoy();
        var clientes = _centros
            .SelectMany(c => c.Documentos
                .Where(d => d.DocumentoId is not null && d.Estado != EstadoDocumento.Vigente
                            && VentanaReclamacion.EsReclamable(d.FechaVencimiento, hoy))
                .Select(d => (c.ClienteId, c.ClienteRazonSocial, DocumentoId: d.DocumentoId!.Value)))
            .GroupBy(x => (x.ClienteId, x.ClienteRazonSocial))
            .Select(g => new ClienteReclamableDto(g.Key.ClienteId, g.Key.ClienteRazonSocial, g.Select(x => x.DocumentoId).Distinct().ToList()))
            .ToList();

        if (clientes.Count == 0)
        {
            var haySinFechaQueNoSePuedeReclamar = _centros.Any(c => c.Documentos.Any(d =>
                d.DocumentoId is not null && d.Estado == EstadoDocumento.SinConfirmar && d.FechaVencimiento is null));
            ToastService.Mostrar(
                Textos[haySinFechaQueNoSePuedeReclamar ? "ToastSinConfirmarSinFechaNoReclamable" : "ToastSinPendientesReclamar"],
                TonoToast.Info);
            return;
        }

        // Con uno o con varios Clientes se abre siempre la confirmación: es un correo a un tercero y no sale sin un «Reclamar» explícito.
        _clientesReclamables = clientes;
        _clientesSeleccionadosReclamar.Clear();
        foreach (var cliente in clientes)
            _clientesSeleccionadosReclamar.Add(cliente.ClienteId);
        _errorReclamar = null;
        _reclamarFaltantesVisible = true;
        // Todos los Clientes empresariales vienen marcados: no es un cambio de quien edita.
        _instantaneaReclamar.Fijar(_clientesSeleccionadosReclamar);
    }

    private bool _reclamacionEmpresaVisible;

    private readonly InstantaneaFormulario _instantaneaReclamar = new();

    /// <summary>
    /// P1-E2b: «hay cambios» de cada modal de la ficha (el guardián de su kit): un tipo de documento ya elegido en el de crear gestión,
    /// o las casillas del de reclamar faltantes cambiadas respecto a cómo se abrió. Cerrados (también tras confirmar) no hay nada que perder.
    /// </summary>
    private bool HayCambiosCrearGestion => _crearGestionVisible && !string.IsNullOrEmpty(_tipoDocumentoParaGestion);

    private bool HayCambiosReclamar => _reclamarFaltantesVisible && _instantaneaReclamar.Difiere(_clientesSeleccionadosReclamar);

    private void AlternarClienteReclamar(Guid clienteId, bool marcado)
    {
        _errorReclamar = null;
        if (marcado) _clientesSeleccionadosReclamar.Add(clienteId);
        else _clientesSeleccionadosReclamar.Remove(clienteId);
    }

    private async Task ConfirmarReclamarFaltantesAsync()
    {
        var seleccionados = _clientesReclamables.Where(c => _clientesSeleccionadosReclamar.Contains(c.ClienteId)).ToList();
        if (seleccionados.Count == 0) return;

        // El modal sigue abierto mientras se envía (con «Guardando»): cerrarlo antes tiraba la selección y escondía el resultado. Se cierra
        // al terminar, salvo que fallen todos los envíos: entonces se queda con la selección y el motivo en su aviso fijo.
        await EnviarReclamacionAClientesAsync(seleccionados, desdeElModal: true);
    }

    /// <summary>
    /// Reutiliza EnviarReclamacionCommand tal cual, un envío por Cliente —
    /// secuencial (no Task.WhenAll) porque el DbContext de la petición Blazor
    /// Server es scoped y no admite uso concurrente.
    /// </summary>
    private async Task EnviarReclamacionAClientesAsync(IReadOnlyList<ClienteReclamableDto> clientes, bool desdeElModal = false)
    {
        if (_reclamandoFaltantes) return;

        // Con el modal abierto durante el envío, la ficha puede cambiar de trabajador antes de que termine: el resultado (modal, aviso fijo)
        // era del anterior y no se escribe sobre el estado del nuevo; los toasts sí salen, que el envío ocurrió.
        var trabajadorDelEnvio = _trabajadorEnPantalla;
        _errorReclamar = null;
        _reclamandoFaltantes = true;
        try
        {
            var enviadosA = new List<string>();
            var fallidos = new List<string>();

            foreach (var cliente in clientes)
            {
                var resultado = await Mediator.Send(new EnviarReclamacionCommand(cliente.ClienteId, cliente.DocumentoIds));
                if (resultado.EsFallido)
                    fallidos.Add($"{cliente.ClienteRazonSocial}: {resultado.Error.Mensaje}");
                else
                    enviadosA.Add(cliente.ClienteRazonSocial);
            }

            if (enviadosA.Count == 1)
                ToastService.Mostrar(Textos["ToastReclamacionEnviadaUno", enviadosA[0]], TonoToast.Exito);
            else if (enviadosA.Count > 1)
                ToastService.Mostrar(Textos["ToastReclamacionEnviadaVarios", enviadosA.Count, string.Join(", ", enviadosA)], TonoToast.Exito);

            var sigueElMismoTrabajador = !_desechado && _trabajadorEnPantalla == trabajadorDelEnvio;

            if (desdeElModal && enviadosA.Count == 0 && sigueElMismoTrabajador)
            {
                _errorReclamar = string.Join(" ", fallidos);
                return;
            }

            foreach (var mensaje in fallidos)
                ToastService.Mostrar(mensaje, TonoToast.Error);

            if (!sigueElMismoTrabajador)
                return;

            if (desdeElModal)
                _reclamarFaltantesVisible = false;

            if (enviadosA.Count > 0)
                await CargarAsync();
        }
        finally
        {
            _reclamandoFaltantes = false;
        }
    }

    private async Task AbrirCrearGestionAsync()
    {
        var carga = _cargaVigente;
        var tipos = await Mediator.Send(new ObtenerTiposDocumentoQuery(AmbitoAplicacion: AmbitoAplicacion.Trabajador), _ciclo.Token);
        if (!EsVigente(carga))
            return;

        _tiposDocumentoDisponibles = tipos;
        _tipoDocumentoParaGestion = string.Empty;
        _errorCrearGestion = null;
        _crearGestionVisible = true;
    }

    private async Task ConfirmarCrearGestionAsync()
    {
        if (_creandoGestion) return;
        if (!Guid.TryParse(_tipoDocumentoParaGestion, out var tipoDocumentoId)) return;

        var trabajadorDelEnvio = _trabajadorEnPantalla;
        _errorCrearGestion = null;
        _creandoGestion = true;
        try
        {
            var resultado = await Mediator.Send(new CrearGestionesParaTrabajadorCommand(TrabajadorId, tipoDocumentoId));
            if (resultado.EsFallido)
            {
                // Si la ficha cambió de trabajador mientras tanto, el rechazo era del anterior: no se escribe en el aviso del nuevo.
                if (_trabajadorEnPantalla == trabajadorDelEnvio)
                    _errorCrearGestion = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar(Textos["ToastGestionCreada", resultado.Valor.Creadas], TonoToast.Exito);
            _crearGestionVisible = false;
            await CargarGestionesAsync();
        }
        finally
        {
            _creandoGestion = false;
        }
    }

    private async Task CompletarGestionAsync(Guid gestionId)
    {
        _completandoGestion.Add(gestionId);
        try
        {
            var resultado = await Mediator.Send(new CompletarGestionCommand(gestionId, Completada: true));
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            await CargarGestionesAsync();
        }
        finally
        {
            _completandoGestion.Remove(gestionId);
        }
    }

    /// <summary>
    /// «Deshacer» del aviso de baja: reabre la misma asignación, con su fecha
    /// de alta de siempre. El comando comprueba otra vez la autoridad y que no
    /// haya otra alta del trabajador en ese centro.
    /// </summary>
    private async Task DeshacerBajaAsignacionAsync(Guid asignacionId)
    {
        if (!_reactivandoAsignacion.Add(asignacionId)) return;

        try
        {
            var resultado = await Mediator.Send(new ReactivarAsignacionCommand(asignacionId));
            if (resultado.EsFallido)
            {
                ToastService.MostrarError(resultado.Error);
                return;
            }

            ToastService.Mostrar(Textos["ToastBajaDeshecha"], TonoToast.Exito);
            await CargarAsync();
        }
        finally
        {
            _reactivandoAsignacion.Remove(asignacionId);
        }
    }

    /// <summary>
    /// Cierra las modales y tira lo que tuvieran preparado. Se llama al cambiar
    /// de trabajador: lo elegido para uno no puede ejecutarse sobre otro.
    /// </summary>
    private void CerrarModalesPendientes()
    {
        _editorFichaVisible = false;
        _corregirDocumentoVisible = false;
        _crearGestionVisible = false;
        _tipoDocumentoParaGestion = string.Empty;
        _errorCrearGestion = null;
        _errorReclamar = null;
        _reclamarFaltantesVisible = false;
        _confirmarBajaAsignacionVisible = false;
        _confirmarBajaDeteccionVisible = false;
        _asignacionABajar = null;
        _clientesReclamables = [];
        _clientesSeleccionadosReclamar.Clear();
        CerrarAsignarCentro();
    }

    // ---------- «Asignar a un centro» (estado vacío de la pestaña Centros) ----------

    private bool _asignarCentroVisible;
    private bool _asignandoCentro;
    private string? _errorAsignarCentro;
    private IReadOnlyList<CentroSelectorDto> _centrosDisponiblesParaAsignar = [];
    private string _centroIdParaAsignar = string.Empty;
    private string _fechaAltaParaAsignar = string.Empty;
    private IReadOnlyList<DocumentoFaltanteDto> _documentosFaltantesParaAsignar = [];
    private readonly InstantaneaFormulario _instantaneaAsignarCentro = new();

    /// <summary>Misma guarda que en el listado: una comprobación de faltantes en vuelo de otro Centro (o de un diálogo ya cerrado) no se pinta.</summary>
    private int _faltantesVigente;

    private bool HayCambiosAsignarCentro => _asignarCentroVisible && _instantaneaAsignarCentro.Difiere(_centroIdParaAsignar, _fechaAltaParaAsignar);

    /// <summary>Los Centros asignables: los que el rol ve y donde este Trabajador no está ya asignado.</summary>
    private IReadOnlyList<OpcionBuscable> OpcionesCentrosParaAsignar => _centrosDisponiblesParaAsignar
        .Where(c => _centros.All(asignado => asignado.CentroId != c.Id))
        .Select(c => new OpcionBuscable(c.Id.ToString(), $"{c.Nombre} ({c.ClienteRazonSocial})"))
        .ToList();

    private bool _abriendoAsignarCentro;

    private async Task AbrirAsignarCentroAsync()
    {
        // Un segundo clic mientras llega el selector no lanza otra apertura que pise a la primera.
        if (_abriendoAsignarCentro || _asignarCentroVisible) return;

        _abriendoAsignarCentro = true;
        try
        {
            ++_faltantesVigente;
            // Una recarga de la ficha durante la espera no la invalida; solo cambiar de Trabajador.
            var trabajador = _trabajadorEnPantalla;
            var centros = await Mediator.Send(new ObtenerCentrosParaSelectorQuery(), _ciclo.Token);
            if (_desechado || _trabajadorEnPantalla != trabajador)
                return;

            _centrosDisponiblesParaAsignar = centros;
            _centroIdParaAsignar = string.Empty;
            _fechaAltaParaAsignar = DiaDeNegocio.Hoy().ToString("yyyy-MM-dd");
            _documentosFaltantesParaAsignar = [];
            _errorAsignarCentro = null;
            _asignarCentroVisible = true;
            // La fecha de alta de hoy viene puesta: no es un cambio de quien edita.
            _instantaneaAsignarCentro.Fijar(_centroIdParaAsignar, _fechaAltaParaAsignar);
        }
        finally
        {
            _abriendoAsignarCentro = false;
        }
    }

    private void CerrarAsignarCentro()
    {
        ++_faltantesVigente;
        _asignarCentroVisible = false;
        _centroIdParaAsignar = string.Empty;
        _documentosFaltantesParaAsignar = [];
        _errorAsignarCentro = null;
    }

    private async Task CambiarCentroParaAsignarAsync(string valor)
    {
        var comprobacion = ++_faltantesVigente;
        _centroIdParaAsignar = valor;
        _errorAsignarCentro = null;

        if (!Guid.TryParse(valor, out var centroId))
        {
            _documentosFaltantesParaAsignar = [];
            return;
        }

        var faltantes = await Mediator.Send(
            new ObtenerDocumentosFaltantesParaAsignacionQuery([TrabajadorId], [centroId]), _ciclo.Token);

        if (comprobacion == _faltantesVigente)
            _documentosFaltantesParaAsignar = faltantes;
    }

    private async Task ConfirmarAsignarCentroAsync()
    {
        if (_asignandoCentro) return;

        if (!Guid.TryParse(_centroIdParaAsignar, out var centroId))
        {
            _errorAsignarCentro = Textos["ErrorSeleccionaCentro"];
            return;
        }

        if (!DateOnly.TryParse(_fechaAltaParaAsignar, out var fechaAlta))
        {
            _errorAsignarCentro = Textos["ErrorFechaAltaInvalida"];
            return;
        }

        var trabajadorDeLaOrden = _trabajadorEnPantalla;
        _asignandoCentro = true;
        _errorAsignarCentro = null;
        try
        {
            var resultado = await Mediator.Send(new CrearAsignacionesCommand([trabajadorDeLaOrden], [centroId], fechaAlta));

            if (resultado.EsFallido)
            {
                _errorAsignarCentro = resultado.Error.Mensaje;
                return;
            }

            var dto = resultado.Valor;
            ToastService.Mostrar(
                dto.YaActivas > 0
                    ? Textos["ToastAsignacionesCreadasConActivas", dto.Creadas, dto.YaActivas].Value
                    : Textos["ToastAsignacionesCreadas", dto.Creadas].Value,
                dto.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);
            // Un rechazo con motivo (p. ej. una Asignación que solapa un periodo ya registrado) se dice, no solo se tiñe de aviso.
            foreach (var error in dto.Errores)
                ToastService.Mostrar(error, TonoToast.Advertencia);

            if (_desechado || _trabajadorEnPantalla != trabajadorDeLaOrden)
                return;

            CerrarAsignarCentro();
            await CargarAsync();
        }
        catch (Exception)
        {
            _errorAsignarCentro = Textos["AsignarCentroError"];
        }
        finally
        {
            _asignandoCentro = false;
        }
    }

    // ---------- Nota interna (mismo patrón que Empresa 360, editor en NotaInternaEditable) ----------

    /// <summary>Manda solo la nota y la <c>Version</c> que la ficha leyó.</summary>
    private Task<CaeManager.Domain.Common.Result> GuardarNotaInternaAsync(string nota) =>
        Mediator.Send(new CaeManager.Application.Trabajadores.Commands.GuardarNotaInternaTrabajador
            .GuardarNotaInternaTrabajadorCommand(_detalle!.Id, nota, _detalle.Version));

    /// <summary>
    /// Relee solo la cabecera (nota y Version) sin el spinner de CargarAsync, que rehará la ficha y perdería el
    /// borrador del editor. Si falla, la ficha se queda con lo que ya enseñaba: el siguiente guardado dará conflicto.
    /// </summary>
    private async Task RecargarTrasNotaInternaAsync()
    {
        var trabajadorId = _detalle?.Id;
        if (trabajadorId is null) return;

        try
        {
            var detalle = await Mediator.Send(new ObtenerTrabajadorPorIdQuery(trabajadorId.Value), _ciclo.Token);
            if (detalle is not null && detalle.Id == _detalle?.Id)
                _detalle = detalle;
        }
        catch (Exception)
        {
            // Sin relectura la Version queda vieja: el siguiente guardado dará conflicto y releerá.
        }
    }
}
