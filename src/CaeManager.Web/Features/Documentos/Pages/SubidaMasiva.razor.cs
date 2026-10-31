using System.Globalization;
using CaeManager.Domain.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.ConfirmarDocumentoPropuestoPorIa;
using CaeManager.Application.Documentos.Queries.DetectarCamposDocumento;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Documentos;
using CaeManager.Web.Features.Documentos.Recursos;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace CaeManager.Web.Features.Documentos.Pages;

/// <summary>
/// "Subida múltiple" (petición del usuario, sesión de trabajo nocturno):
/// arrastrar varios archivos de golpe, o un único .zip con varios dentro, y
/// dar de alta un Documento de Trabajador por cada uno. Ámbito acotado a
/// Trabajador — mismo criterio que Fase 38 (Revisión IA): es el único
/// ámbito que <see cref="DetectarCamposDocumentoQuery"/> sabe resolver
/// (empareja por DNI), y no se pidió Cliente/Empresa esta vez.
///
/// Cada archivo se convierte a PDF (mismo <see cref="ConversorArchivosPdf"/>
/// que el alta individual) y se le ejecuta la detección de la Fase 54. La IA
/// solo PROPONE —Trabajador, tipo y fechas leídas del archivo— y todo archivo
/// queda pendiente de confirmar: el Documento no existe hasta que una persona
/// lo confirma o lo corrige (decisión del propietario, 2026-09-19; riesgo R-1
/// del inventario de funciones de IA). La fecha de emisión propuesta es la
/// leída del archivo y, si no se leyó, el campo queda vacío: nunca "hoy" por
/// defecto. La confianza autorreportada se muestra como dato, nunca como
/// permiso: ninguna ruta de este fichero crea un Documento sin pasar por
/// <see cref="ConfirmarAsync"/>. La maqueta de Design (2026-10-18) enseña un
/// estado «creado solo» con confianza ≥ 95 %: es el comportamiento que R-1
/// retiró y no se reintroduce sin una decisión nueva del propietario.
///
/// Flujo por archivo (maqueta «Subida Masiva TALVEG», 2026-10-18): En cola →
/// Subiendo (barra de bytes leídos, como mucho <see cref="SubidasSimultaneas"/> a
/// la vez) → Leyendo (conversión a PDF y detección, de una en una porque cada
/// lectura es una llamada a la IA) → pendiente de confirmar. Mientras el lote
/// se lee se pueden soltar más archivos; mientras sube, no.
///
/// Visor: miniatura (primera página rasterizada a PNG con el mismo
/// <see cref="IRasterizadorPaginasPdfService"/> que ya usa el Caso Mixto de
/// IA documental) para poder reconocer cada archivo de un vistazo en la
/// lista sin abrir nada — cargar N PDFs completos a la vez sería
/// notablemente más lento. Al gestionar uno, el PDF completo se ve en un
/// &lt;iframe&gt; con el propio contenido en base64, que solo existe para la
/// fila abierta y se suelta al cerrarla (<see cref="_vistaPrevia"/>): los archivos pendientes
/// de confirmar deliberadamente no se guardan en <see cref="IFileStorageService"/>
/// todavía (evita huérfanos si se descartan, y evita abrir un endpoint que
/// sirva un archivo por clave sin un Documento que autorizar contra él —
/// el mismo vector IDOR que Fase 31/Issue #18 ya cerraron para el resto de
/// la aplicación). Ningún archivo se persiste sin la confirmación humana.
///
/// Mientras esperan, los archivos no viven en memoria del circuito sino en
/// temporales de <see cref="AlmacenTemporalSubidaMasiva"/>, con un tope de
/// bytes retenidos por pantalla, por Tenant de origen y por proceso
/// (<see cref="OpcionesSubidaMasiva"/>; S-C-3, ASVS 4.0.3 V12.1.1). En memoria
/// solo están el archivo que se lee en ese momento, la vista previa abierta y
/// las miniaturas, reducidas y descontadas del mismo tope. Lo que no cabe se
/// marca con error sin leerse; cerrar la pantalla borra todos sus temporales.
///
/// El <c>[Authorize]</c> de la página incluye <see cref="Roles.Consulta"/> —
/// a quién se le deja abrir esta pantalla no lo decide este fichero. Pero
/// Consulta es de solo lectura (<see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>
/// no lo admite para <see cref="ConfirmarDocumentoPropuestoPorIaCommand"/>), así que aquí
/// dentro no se le pinta ningún disparador de escritura (<see cref="_esSoloLectura"/>)
/// y <see cref="CrearDocumentoDelItemAsync"/> comprueba la capacidad ANTES
/// de llamar a <see cref="IFileStorageService.GuardarAsync"/>: sin esto, un
/// Consulta podía dejar el PDF ya escrito en almacenamiento aunque el
/// Command fuera a fallar siempre, con el mismo huérfano de mejor esfuerzo
/// que <see cref="DescartarArchivoHuerfanoAsync"/> ya documenta para el
/// caso general. El alcance de la Asignación de Cartera no se decide aquí: el
/// selector de Trabajador ya viene acotado a la cartera, y el alta
/// (<c>CrearDocumentoCommand</c>, que <see cref="ConfirmarDocumentoPropuestoPorIaCommand"/>
/// hereda) rechaza como «no existe» un Trabajador fuera de ella.
/// </summary>
public partial class SubidaMasiva : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    private const int MaximoArchivosPorLote = 60;

    /// <summary>Subidas de bytes a la vez; el resto de archivos espera en cola (maqueta, 2026-10-17).</summary>
    private const int SubidasSimultaneas = 2;

    /// <summary>Ancho de la miniatura: tres veces los 48 px a los que se pinta, para pantallas de alta densidad.</summary>
    private const int AnchoMiniaturaPixeles = 144;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private CuotaSubidaMasiva CuotaSubidaMasiva { get; set; } = default!;
    [Inject] private IPresupuestoMemoriaDocumentos PresupuestoMemoria { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private IFileStorageService AlmacenamientoArchivos { get; set; } = default!;
    [Inject] private IConversorWordPdfService ConversorWordPdf { get; set; } = default!;
    [Inject] private IRasterizadorPaginasPdfService Rasterizador { get; set; } = default!;
    [Inject] private ILogger<SubidaMasiva> Logger { get; set; } = default!;
    [Inject] private ICurrentUserService CurrentUserService { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosSubidaMasiva> Textos { get; set; } = default!;

    private enum EstadoItem { Cola, Subiendo, Procesando, PendienteConfirmar, Creado, Descartado, Error }

    private sealed class ItemLote
    {
        public Guid ClaveLocal { get; } = Guid.NewGuid();
        public required string NombreArchivo { get; init; }
        public int LoteId { get; set; }

        /// <summary>Referencia del navegador mientras el archivo espera o sube; se suelta en cuanto sus bytes están en memoria.</summary>
        public IBrowserFile? Archivo { get; set; }
        public long TamanoBytes { get; set; }
        public long BytesSubidos { get; set; }

        /// <summary>Nombre del .zip del que salió el archivo, si salió de uno.</summary>
        public string? Origen { get; set; }
        public Guid? DocumentoId { get; set; }

        /// <summary>El PDF ya convertido, en un temporal: se borra al confirmar, al descartar o al cerrar la pantalla.</summary>
        public ArchivoTemporal? Pdf { get; set; }

        /// <summary>URI <c>data:</c> de la miniatura; sus bytes descuentan del tope de la pantalla (<see cref="BytesMiniatura"/>).</summary>
        public string? MiniaturaDataUri { get; set; }
        public long BytesMiniatura { get; set; }
        public EstadoItem Estado { get; set; } = EstadoItem.Procesando;
        public string TrabajadorId { get; set; } = string.Empty;
        public string TipoDocumentoId { get; set; } = string.Empty;

        /// <summary>
        /// Vacía hasta que la IA lea una del archivo o la persona la escriba:
        /// nunca "hoy" por defecto. Un valor puesto de oficio se confirmaba con
        /// un clic y daba un Documento vigente con una fecha que nadie leyó.
        /// </summary>
        public string FechaEmision { get; set; } = string.Empty;
        public string FechaVencimientoManual { get; set; } = string.Empty;

        /// <summary>
        /// Confirmación expresa de la persona de que el documento no caduca. La
        /// IA nunca la propone: sin fecha y sin esta marca, la vigencia queda
        /// sin confirmar.
        /// </summary>
        public bool NoCaduca { get; set; }

        /// <summary>Lo que la IA propuso, intacto: es el término de comparación de lo que la persona confirma.</summary>
        public PropuestaIaDocumento Propuesta { get; set; } = PropuestaIaDocumento.Vacia;
        public int? Confianza { get; set; }
        public string? MensajeError { get; set; }
        public bool Expandido { get; set; }
        public bool Confirmando { get; set; }
    }

    private readonly List<ItemLote> _items = [];

    /// <summary>Los temporales de esta pantalla; nulo hasta OnInitializedAsync. Se desecha en <see cref="Dispose"/>.</summary>
    private AlmacenTemporalSubidaMasiva? _almacen;

    /// <summary>El PDF en base64 de la única fila abierta, o nada: se construye al abrirla y se suelta al cerrarla.</summary>
    private (Guid Clave, string DataUri)? _vistaPrevia;

    /// <summary>Lo que <see cref="_vistaPrevia"/> descuenta del presupuesto de memoria del proceso mientras existe.</summary>
    private ReservaMemoriaDocumentos? _reservaVistaPrevia;

    /// <summary>
    /// Veces el tamaño del original que una lectura tiene a la vez en memoria: el
    /// original, su PDF, lo que la IA prepara a partir de él (texto, páginas
    /// rasterizadas y su base64) y la miniatura. Se reserva entero del presupuesto del
    /// proceso antes de cargar el original (seguimiento de S-C-3, ASVS 4.0.3 V12.1.1).
    /// El alta individual de <c>DrawerGestionDocumento</c> usa el mismo factor.
    /// </summary>
    internal const int FactorMemoriaLectura = 4;
    private bool _procesandoLote;
    private bool _cargando = true;
    private bool _errorCarga;
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;
    private int _cargaVigente;
    private int _ultimoLote;

    /// <summary>Una sola lectura (conversión + IA) a la vez en toda la pantalla, aunque se suelten lotes seguidos.</summary>
    private readonly SemaphoreSlim _turnoLectura = new(1, 1);
    private const string FiltroTodos = "todos";
    private string _filtro = FiltroTodos;
    private (string Titulo, string Detalle)? _errorLote;
    private ItemLote? _paraDescartar;
    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresDisponibles = [];
    private IReadOnlyList<TipoDocumentoListaDto> _tiposDisponibles = [];

    /// <summary>
    /// Consulta es el único rol de solo lectura que el <c>[Authorize]</c> de
    /// esta página admite (ver el comentario de clase) — comprobarlo contra
    /// ESE rol, y no contra una lista de roles con escritura duplicada aquí,
    /// mantiene esta comprobación correcta aunque el día de mañana alguien
    /// amplíe la lista blanca de <c>AutorizacionEscrituraBehavior</c> en otro
    /// fichero: seguiría sin admitir Consulta y este campo seguiría siendo
    /// exacto sin tocarlo.
    /// </summary>
    private bool _esSoloLectura;
    private int _totalRecibidos;
    private int _totalCreados;
    private int _totalDescartados;
    private int _totalErrores;

    private static readonly (string Valor, string Clave)[] Filtros =
    [
        (FiltroTodos, "FiltroTodos"),
        ("pendientes", "FiltroPendientes"),
        ("errores", "FiltroErrores"),
        ("creados", "FiltroCreados")
    ];

    private IReadOnlyList<OpcionBuscable> OpcionesTrabajadores => EtiquetasSelectorTrabajador.Construir(_trabajadoresDisponibles, aliasSiempre: true, empleadorSiempre: true)
        .Select(e => new OpcionBuscable(e.Id.ToString(), e.Texto))
        .ToList();

    private int TotalCreados => _totalCreados;
    private int TotalPendientes => _items.Count(i => i.Estado == EstadoItem.PendienteConfirmar);
    private int TotalDescartados => _totalDescartados;
    private int TotalErrores => _totalErrores;
    private int TotalRecibidos => _totalRecibidos;
    private int ArchivosEnCurso => _items.Count(i => i.Estado is EstadoItem.Cola or EstadoItem.Subiendo or EstadoItem.Procesando);
    private bool HayArchivosEnCurso => ArchivosEnCurso > 0;

    /// <summary>
    /// FS-14: lo que se perdería al salir. En cola, subiéndose, en curso (leyéndose), sin confirmar
    /// (propuesta de la IA a la vista) o confirmándose en este momento: nada de
    /// eso existe todavía fuera de la memoria del circuito.
    /// </summary>
    private int ArchivosSinGuardar => _items.Count(i =>
        i.Estado is EstadoItem.Cola or EstadoItem.Subiendo or EstadoItem.Procesando or EstadoItem.PendienteConfirmar || i.Confirmando);

    private bool HayTrabajoSinGuardar => ArchivosSinGuardar > 0;
    private int ArchivosLeidos => TotalRecibidos - ArchivosEnCurso;
    private int PorcentajeProgreso => TotalRecibidos == 0 ? 0 : ArchivosLeidos * 100 / TotalRecibidos;
    private IReadOnlyList<ItemLote> ItemsFiltrados => _items.Where(item => _filtro switch
    {
        "pendientes" => item.Estado == EstadoItem.PendienteConfirmar,
        "errores" => item.Estado == EstadoItem.Error,
        "creados" => item.Estado == EstadoItem.Creado,
        _ => true
    }).ToList();

    protected override async Task OnInitializedAsync()
    {
        // Por la puerta de acceso a datos: dentro de un Workspace operativo derivado el rol
        // efectivo se resuelve contra la cartera (consulta al DbContext) y esta página se
        // inicializa en paralelo con el layout, que comparte ese DbContext. Sin la puerta
        // lanzaba «A second operation was started on this context instance» (HTTP 500).
        _esSoloLectura = await PuertaAccesoDatos.EjecutarAsync(
            () => CurrentUserService.ObtenerRolEfectivoAsync()) == Roles.Consulta;
        // El Tenant de origen solo reparte el tope de bytes retenidos (quién
        // consume el recurso); no autoriza nada ni decide a qué Tenant van los Documentos.
        var tenantOrigenId = await PuertaAccesoDatos.EjecutarAsync(
            () => CurrentUserService.ObtenerTenantOrigenIdAsync());
        if (!_desechado)
            _almacen = CuotaSubidaMasiva.CrearAlmacen(tenantOrigenId);
        await CargarCatalogosAsync();
    }

    /// <summary>Trabajadores de la cartera y tipos de ámbito Trabajador; si falla, la pantalla enseña «No pudimos cargar la subida» con Reintentar.</summary>
    private async Task CargarCatalogosAsync()
    {
        var carga = ++_cargaVigente;
        var token = _ciclo.Token;
        _errorCarga = false;
        _cargando = true;
        try
        {
            var trabajadores = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery(AlcanceSelectorTrabajadores.Cartera), token);
            if (!EsVigente(carga)) return;
            var tipos = await Mediator.Send(new ObtenerTiposDocumentoQuery(AmbitoAplicacion: AmbitoAplicacion.Trabajador, ParaAltaNueva: true), token);
            if (!EsVigente(carga)) return;
            _trabajadoresDisponibles = trabajadores;
            _tiposDisponibles = tipos;
            _cargando = false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "No se pudieron cargar los datos de la subida múltiple.");
            if (!EsVigente(carga)) return;
            _cargando = false;
            _errorCarga = true;
        }
    }

    private async Task ManejarArchivosSeleccionadosAsync(InputFileChangeEventArgs e)
    {
        // La zona de soltar no se pinta para _esSoloLectura (ver el marcado),
        // pero el guarda va también aquí: es el punto de entrada real de
        // cualquier InputFileChangeEventArgs, venga de donde venga.
        if (_esSoloLectura) return;

        if (_desechado || _almacen is null || _procesandoLote || HayArchivosEnCurso)
            return;

        var carga = _cargaVigente;
        var token = _ciclo.Token;
        _errorLote = null;

        if (e.FileCount > MaximoArchivosPorLote)
        {
            _errorLote = (Textos["ErrorLoteCantidad", MaximoArchivosPorLote], Textos["ErrorLoteDetalleCantidad"]);
            StateHasChanged();
            return;
        }

        var archivosSeleccionados = e.GetMultipleFiles(MaximoArchivosPorLote);
        var lote = ++_ultimoLote;
        var lecturas = new List<Task>();

        _procesandoLote = true;
        StateHasChanged();

        try
        {
            // El presupuesto es la reserva del almacén de la pantalla, común a
            // todos sus lotes: cada archivo que entra —suelto o salido de un
            // .zip— reserva su tamaño declarado antes de leerse y lo devuelve
            // al confirmarse o descartarse. Sin tope, el de 10 MB acotaba el
            // .zip comprimido pero no lo que salía de él (factor 1027 medido
            // sobre ceros), y los lotes se sumaban sin límite.
            var cola = new Queue<ItemLote>();

            foreach (var archivo in archivosSeleccionados)
            {
                // El tamaño que declara el navegador es el mismo dato que
                // OpenReadStream compara con maxAllowedSize antes de leer nada.
                // Sin esta comprobación lanzaba una IOException que nadie
                // capturaba: el archivo no llegaba a la lista, no había aviso,
                // y los válidos del mismo lote ya leídos se perdían con él
                // (defecto del 2026-09-22). Ahora es un error de ese archivo y
                // el lote sigue.
                if (archivo.Size > LimitesArchivoSubido.TamanoMaximoBytes)
                {
                    var excedido = NuevoItemError(archivo.Name, TextoArchivoSuperaLimite(archivo.Name));
                    excedido.LoteId = lote;
                    AgregarItem(excedido);
                    continue;
                }

                var item = new ItemLote
                {
                    NombreArchivo = archivo.Name,
                    Estado = EstadoItem.Cola,
                    Archivo = archivo,
                    TamanoBytes = archivo.Size,
                    LoteId = lote
                };
                AgregarItem(item);
                cola.Enqueue(item);
            }

            StateHasChanged();

            await Task.WhenAll(Enumerable.Range(0, SubidasSimultaneas)
                .Select(_ => SubirDeLaColaAsync(cola, lecturas, lote, carga, token)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            if (EsVigente(carga))
            {
                _procesandoLote = false;
                StateHasChanged();
            }
        }

        // Cota global: no entra un lote nuevo mientras haya archivos en cola, subiéndose o leyéndose (HayArchivosEnCurso).
        try
        {
            await Task.WhenAll(lecturas);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task SubirDeLaColaAsync(
        Queue<ItemLote> cola, List<Task> lecturas, int lote, int carga, CancellationToken token)
    {
        while (EsVigente(carga) && cola.TryDequeue(out var item))
            await SubirAsync(item, lecturas, lote, carga, token);
    }

    private async Task SubirAsync(
        ItemLote item, List<Task> lecturas, int lote, int carga, CancellationToken token)
    {
        if (item.Archivo is not { } archivo || _almacen is not { } almacen)
            return;

        item.Estado = EstadoItem.Subiendo;
        StateHasChanged();

        ArchivoTemporal contenido;
        try
        {
            contenido = await LeerBytesAsync(almacen, item, archivo, carga, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (CuotaSubidaMasivaSuperadaException)
        {
            // Rechazado antes de leerlo: no cabe en lo que la pantalla, su
            // Tenant de origen o el servidor retienen ya.
            if (!EsVigente(carga)) return;
            MarcarError(item, Textos["ErrorSinEspacio"]);
            StateHasChanged();
            return;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "No se pudo leer {ReferenciaArchivo} del navegador en la subida múltiple.",
                ReferenciaArchivoTraza.De(item.NombreArchivo));
            if (!EsVigente(carga)) return;
            MarcarError(item, Textos["ErrorSubida"]);
            StateHasChanged();
            return;
        }

        if (!EsVigente(carga))
        {
            almacen.Eliminar(contenido);
            return;
        }

        item.Archivo = null;

        if (ExtractorZip.EsZip(item.NombreArchivo))
        {
            await AbrirZipAsync(almacen, item, contenido, lecturas, lote, carga, token);
            return;
        }

        item.Estado = EstadoItem.Procesando;
        StateHasChanged();
        lecturas.Add(LeerEnSerieAsync(item, contenido, carga, token));
    }

    /// <summary>
    /// Copia el archivo del navegador a un temporal en trozos y deja en el item los bytes ya leídos:
    /// es la barra de «MB de MB» de la maqueta (el evento de progreso de la subida). El tamaño
    /// declarado se reserva antes de abrir el flujo, así que lo que no cabe no se llega a pedir al navegador.
    /// </summary>
    private async Task<ArchivoTemporal> LeerBytesAsync(
        AlmacenTemporalSubidaMasiva almacen, ItemLote item, IBrowserFile archivo, int carga, CancellationToken token)
    {
        var ultimoPorcentaje = PorcentajeSubida(item);
        return await almacen.GuardarAsync(
            () => archivo.OpenReadStream(LimitesArchivoSubido.TamanoMaximoBytes, token),
            archivo.Size,
            LimitesArchivoSubido.TamanoMaximoBytes,
            leidos =>
            {
                item.BytesSubidos = leidos;
                var porcentaje = PorcentajeSubida(item);
                if (porcentaje != ultimoPorcentaje && EsVigente(carga))
                {
                    ultimoPorcentaje = porcentaje;
                    StateHasChanged();
                }
            },
            token);
    }

    /// <summary>
    /// Un .zip no es un documento: se abre y su fila se sustituye por una por cada archivo de dentro
    /// («Salió de lote.zip»). Si no cabe en lo que queda del lote, o está dañado, la fila del .zip
    /// se queda en Error y el resto del lote sigue.
    /// </summary>
    private async Task AbrirZipAsync(
        AlmacenTemporalSubidaMasiva almacen, ItemLote zip, ArchivoTemporal contenido, List<Task> lecturas, int lote, int carga, CancellationToken token)
    {
        IReadOnlyList<(ArchivoTemporal Archivo, string NombreArchivo)> entradasZip;
        try
        {
            if (!ValidadorFirmaArchivo.TieneFirmaValida(await almacen.LeerCabeceraAsync(contenido, 8, token), zip.NombreArchivo))
            {
                if (!EsVigente(carga)) return;
                MarcarError(zip, Textos["ErrorZipInvalido"]);
                StateHasChanged();
                return;
            }

            zip.Estado = EstadoItem.Procesando;
            using var flujoZip = almacen.AbrirLectura(contenido);
            entradasZip = ExtractorZip.Extraer(
                flujoZip,
                // Lo que quepa aún en el lote: un .zip no puede traer más
                // archivos de los que faltan para el tope, y ese recuento no
                // se hace después de haberlos expandido. La fila del propio
                // .zip se va a sustituir, por eso no cuenta.
                MaximoArchivosPorLote - (_items.Count(i => i.LoteId == lote) - 1),
                LimitesArchivoSubido.TamanoMaximoBytes,
                almacen);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (LimiteDeZipSuperadoException ex) when (EsVigente(carga))
        {
            // Límite superado: es el caso previsto, no un fallo. El .zip se
            // descarta entero y el lote continúa con el resto de archivos. Solo
            // el mensaje de ESTE tipo se enseña: un .zip dañado lanza
            // InvalidDataException con texto técnico del framework, que va al
            // bloque de abajo.
            Logger.LogWarning(
                "Se descartó un .zip que supera los límites de descompresión en la subida múltiple: {Motivo}",
                ex.Message);
            MarcarError(zip, ex.Message);
            StateHasChanged();
            return;
        }
        catch (Exception ex)
        {
            if (!EsVigente(carga)) return;
            Logger.LogWarning(ex, "No se pudo leer un .zip en la subida múltiple.");
            MarcarError(zip, Textos["ErrorZipIlegible"]);
            StateHasChanged();
            return;
        }
        finally
        {
            // El .zip ya no hace falta: lo que importa son sus entradas, cada
            // una con su temporal y su reserva.
            almacen.Eliminar(contenido);
        }

        if (!EsVigente(carga))
        {
            foreach (var (extraido, _) in entradasZip)
                almacen.Eliminar(extraido);
            return;
        }

        var posicion = _items.IndexOf(zip);
        _items.RemoveAt(posicion);
        _totalRecibidos--;
        foreach (var (contenidoEntrada, nombreEntrada) in entradasZip)
        {
            var entrada = new ItemLote
            {
                NombreArchivo = nombreEntrada,
                Estado = EstadoItem.Procesando,
                LoteId = lote,
                Origen = zip.NombreArchivo
            };
            _items.Insert(posicion++, entrada);
            _totalRecibidos++;
            lecturas.Add(LeerEnSerieAsync(entrada, contenidoEntrada, carga, token));
        }

        StateHasChanged();
    }

    /// <summary>
    /// Espera su turno de lectura con el original en un temporal; solo dentro del turno pasa a memoria,
    /// así que el circuito nunca sostiene más de un original a la vez. El temporal se borra siempre.
    /// </summary>
    private async Task LeerEnSerieAsync(ItemLote item, ArchivoTemporal original, int carga, CancellationToken token)
    {
        try
        {
            await _turnoLectura.WaitAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _almacen?.Eliminar(original);
            return;
        }

        try
        {
            // El original no se carga hasta tener hueco en el presupuesto del proceso
            // para él y para todo lo que la lectura deriva de él; la reserva se
            // devuelve al terminar la lectura, también si falla.
            ReservaMemoriaDocumentos reserva;
            try
            {
                reserva = await ReservarMemoriaAsync(original.TamanoBytes * FactorMemoriaLectura, token);
            }
            catch (Exception ex)
            {
                _almacen?.Eliminar(original);
                if (ex is not CuotaSubidaMasivaSuperadaException) throw;
                if (!EsVigente(carga)) return;
                MarcarError(item, Textos["ErrorSinEspacio"]);
                StateHasChanged();
                return;
            }

            using (reserva)
            {
                byte[] contenido;
                try
                {
                    contenido = await _almacen!.LeerAsync(original, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (!EsVigente(carga)) return;
                    Logger.LogWarning(ex, "No se pudo recuperar {ReferenciaArchivo} del temporal en la subida múltiple.",
                        ReferenciaArchivoTraza.De(item.NombreArchivo));
                    MarcarError(item, Textos["ErrorSubida"]);
                    StateHasChanged();
                    return;
                }
                finally
                {
                    _almacen?.Eliminar(original);
                }

                await LeerItemAsync(item, contenido, carga, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            _turnoLectura.Release();
        }
    }

    /// <summary>Valida el archivo, lo convierte a PDF y le pide a la IA su propuesta; el item acaba pendiente de confirmar o en Error.</summary>
    private async Task LeerItemAsync(ItemLote item, byte[] contenido, int carga, CancellationToken token)
    {
        var nombreArchivo = item.NombreArchivo;
        if (!ConversorArchivosPdf.EsPdf(nombreArchivo) && !ConversorArchivosPdf.EsImagen(nombreArchivo) && !ConversorArchivosPdf.EsWord(nombreArchivo))
        {
            MarcarError(item, Textos["ErrorFormato"]);
            StateHasChanged();
            return;
        }

        if (contenido.Length > LimitesArchivoSubido.TamanoMaximoBytes)
        {
            MarcarError(item, TextoArchivoSuperaLimite(nombreArchivo));
            StateHasChanged();
            return;
        }

        if (!ValidadorFirmaArchivo.EsDocumentoValido(contenido, nombreArchivo))
        {
            MarcarError(item, Textos["ErrorContenido"]);
            StateHasChanged();
            return;
        }

        byte[] contenidoPdf;
        try
        {
            contenidoPdf = await ConversorArchivosPdf.UnificarAsync([(contenido, nombreArchivo)], ConversorWordPdf);
            if (!EsVigente(carga)) return;
        }
        catch (ConversionWordPdfOcupadaException)
        {
            if (!EsVigente(carga)) return;
            MarcarError(item, Textos["ErrorConversionOcupada"]);
            StateHasChanged();
            return;
        }
        catch (Exception ex)
        {
            if (!EsVigente(carga)) return;
            Logger.LogWarning(ex, "No se pudo convertir {ReferenciaArchivo} a PDF en la subida múltiple.",
                ReferenciaArchivoTraza.De(nombreArchivo));
            MarcarError(item, Textos["ErrorConvertir"]);
            StateHasChanged();
            return;
        }

        // El PDF espera a la confirmación en un temporal, no en el circuito. Si
        // no cabe en lo que la pantalla ya retiene, el archivo se queda en
        // Error antes de gastar una lectura de la IA en él.
        if (_almacen is not { } almacen) return;
        try
        {
            item.Pdf = await almacen.GuardarAsync(contenidoPdf, token);
        }
        catch (CuotaSubidaMasivaSuperadaException)
        {
            if (!EsVigente(carga)) return;
            MarcarError(item, Textos["ErrorSinEspacio"]);
            StateHasChanged();
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!EsVigente(carga)) return;
            Logger.LogWarning(ex, "No se pudo guardar el temporal de {ReferenciaArchivo} en la subida múltiple.",
                ReferenciaArchivoTraza.De(nombreArchivo));
            MarcarError(item, Textos["ErrorConvertir"]);
            StateHasChanged();
            return;
        }

        PonerMiniatura(item, contenidoPdf);

        var deteccion = await DetectarCamposAsync(contenidoPdf, nombreArchivo, token);
        if (!EsVigente(carga)) return;
        item.Confianza = deteccion?.ConfianzaGeneral;

        var tipoResuelto = deteccion?.TipoDocumentoId is { } tipoId && _tiposDisponibles.Any(t => t.Id == tipoId) ? tipoId : (Guid?)null;
        var trabajadorResuelto = deteccion?.TrabajadorId is { } trabId && _trabajadoresDisponibles.Any(t => t.Id == trabId) ? trabId : (Guid?)null;

        // La IA propone; ni la confianza ni una coincidencia completa la
        // habilitan para crear nada. Todo archivo queda pendiente de una
        // persona, con la propuesta ya prellenada.
        item.Propuesta = new PropuestaIaDocumento(
            trabajadorResuelto, tipoResuelto, deteccion?.FechaEmisionLeida, deteccion?.FechaVencimientoPropuesta, deteccion?.ConfianzaGeneral ?? 0);
        item.TipoDocumentoId = tipoResuelto?.ToString() ?? string.Empty;
        item.TrabajadorId = trabajadorResuelto?.ToString() ?? string.Empty;
        item.FechaEmision = deteccion?.FechaEmisionLeida?.ToString("yyyy-MM-dd") ?? string.Empty;
        item.FechaVencimientoManual = deteccion?.FechaVencimientoPropuesta?.ToString("yyyy-MM-dd") ?? string.Empty;
        item.Estado = EstadoItem.PendienteConfirmar;

        // Feedback incremental: con un lote de varios archivos, esperar al
        // StateHasChanged de después del foreach entero dejaría la lista
        // congelada en "Leyendo…" hasta que termina el último.
        if (EsVigente(carga))
            StateHasChanged();
    }

    private string TextoArchivoSuperaLimite(string nombreArchivo) =>
        Textos["ArchivoSuperaLimite", nombreArchivo, LimitesArchivoSubido.TamanoMaximoMb];

    private static ItemLote NuevoItemError(string nombreArchivo, string mensaje) => new()
    {
        NombreArchivo = nombreArchivo,
        Estado = EstadoItem.Error,
        MensajeError = mensaje
    };

    /// <summary>
    /// Mejor esfuerzo — sin miniatura, el item sigue funcionando igual, solo sin imagen en la lista.
    /// La URI se guarda ya construida (no se rehace en cada render) y sus bytes en memoria
    /// —dos por carácter— descuentan del tope de la pantalla; si no caben, no hay miniatura.
    /// </summary>
    private void PonerMiniatura(ItemLote item, byte[] contenidoPdf)
    {
        if (GenerarMiniatura(contenidoPdf, item.NombreArchivo) is not { } base64 || _almacen is not { } almacen)
            return;

        var dataUri = $"data:image/png;base64,{base64}";
        var bytes = (long)dataUri.Length * sizeof(char);
        if (!almacen.TryReservar(bytes))
            return;

        item.MiniaturaDataUri = dataUri;
        item.BytesMiniatura = bytes;
    }

    private void SoltarMiniatura(ItemLote item)
    {
        _almacen?.Liberar(item.BytesMiniatura);
        item.BytesMiniatura = 0;
        item.MiniaturaDataUri = null;
    }

    /// <summary>Borra el temporal del PDF y suelta su vista previa, si estaba abierta.</summary>
    private void SoltarPdf(ItemLote item)
    {
        if (item.Pdf is { } pdf)
            _almacen?.Eliminar(pdf);
        item.Pdf = null;
        if (_vistaPrevia?.Clave == item.ClaveLocal)
            SoltarVistaPrevia();
    }

    /// <summary>Suelta la vista previa abierta y devuelve su reserva de memoria.</summary>
    private void SoltarVistaPrevia()
    {
        _vistaPrevia = null;
        _reservaVistaPrevia?.Dispose();
        _reservaVistaPrevia = null;
    }

    /// <summary>
    /// Reserva del presupuesto de memoria del proceso. Si no hay hueco dentro de la
    /// espera máxima, falla igual que la cuota de la pantalla, del Tenant de origen o
    /// del proceso (<see cref="CuotaSubidaMasivaSuperadaException"/>), y el archivo se
    /// queda en Error con el mismo mensaje.
    /// </summary>
    private async Task<ReservaMemoriaDocumentos> ReservarMemoriaAsync(long bytes, CancellationToken token)
    {
        try
        {
            return await PresupuestoMemoria.ReservarAsync(bytes, token);
        }
        catch (PresupuestoMemoriaAgotadoException)
        {
            throw new CuotaSubidaMasivaSuperadaException();
        }
    }

    private string? GenerarMiniatura(byte[] contenidoPdf, string nombreArchivo)
    {
        var resultado = Rasterizador.RasterizarMiniatura(contenidoPdf, 0, AnchoMiniaturaPixeles);
        if (resultado.EsFallido)
        {
            Logger.LogInformation(
                // ReferenciaArchivoTraza y no el nombre: los nombres reales
                // llegan con nombre, DNI y naturaleza médica dentro (#360). El
                // caso "sin páginas" desaparece porque RasterizarPagina
                // devuelve una imagen o un fallo, no una lista que pueda venir
                // vacía.
                "No se pudo generar miniatura para {ReferenciaArchivo} en la subida múltiple: {Error}",
                ReferenciaArchivoTraza.De(nombreArchivo),
                resultado.Error.Mensaje);
            return null;
        }

        return Convert.ToBase64String(resultado.Valor);
    }

    /// <summary>Mejor esfuerzo, mismo criterio que el alta individual (Fase 54): si falla, el item sigue pendiente de confirmar a mano.</summary>
    private async Task<DeteccionCamposDocumentoDto?> DetectarCamposAsync(byte[] contenidoPdf, string nombreArchivo, CancellationToken token)
    {
        try
        {
            var resultado = await Mediator.Send(new DetectarCamposDocumentoQuery(contenidoPdf, nombreArchivo, AmbitoAplicacion.Trabajador), token);
            return resultado.EsFallido ? null : resultado.Valor;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "No se pudo completar la detección automática para {ReferenciaArchivo} en la subida múltiple.",
                ReferenciaArchivoTraza.De(nombreArchivo));
            return null;
        }
    }

    /// <summary>
    /// Como mucho un item expandido a la vez — mismo criterio que
    /// RevisionIa.razor: evita cargar N &lt;iframe&gt; de PDF en base64 (pesados
    /// de por sí) si el usuario despliega varias filas seguidas. Por lo mismo,
    /// el base64 del PDF solo existe para la fila abierta (<see cref="_vistaPrevia"/>).
    /// </summary>
    private async Task AlternarExpandidoAsync(ItemLote item)
    {
        var expandir = !item.Expandido;
        foreach (var otro in _items) otro.Expandido = false;
        SoltarVistaPrevia();
        item.Expandido = expandir;
        if (!expandir || item.Pdf is not { } pdf || _almacen is not { } almacen)
            return;

        ReservaMemoriaDocumentos? reserva = null;
        try
        {
            // El PDF y su data URI (base64, dos bytes por carácter) viven en memoria
            // mientras la fila está abierta: se reservan antes de leer y se devuelven
            // al cerrarla (SoltarVistaPrevia).
            // El token se toma antes de esperar: Dispose desecha _ciclo.
            var token = _ciclo.Token;
            reserva = await ReservarMemoriaAsync(pdf.TamanoBytes + BytesDataUriPdf(pdf.TamanoBytes), token);
            var contenido = await almacen.LeerAsync(pdf, token);
            // Si mientras se leía se abrió otra fila o se cerró esta, la vista no es de nadie.
            if (item.Expandido && item.Pdf == pdf && !_desechado)
            {
                SoltarVistaPrevia();
                _vistaPrevia = (item.ClaveLocal, ConstruirDataUriPdf(contenido));
                _reservaVistaPrevia = reserva;
                reserva = null;
            }
        }
        catch (CuotaSubidaMasivaSuperadaException)
        {
            // Sin vista previa la fila se puede confirmar igual.
            ToastService.Mostrar(Textos["ErrorSinEspacio"], TonoToast.Error);
        }
        catch (OperationCanceledException)
        {
            // La pantalla se cerró mientras se leía.
        }
        catch (Exception ex)
        {
            // Sin vista previa la fila se puede confirmar igual; el temporal ya no estaba.
            Logger.LogWarning(ex, "No se pudo leer la vista previa de {ReferenciaArchivo} en la subida múltiple.",
                ReferenciaArchivoTraza.De(item.NombreArchivo));
        }
        finally
        {
            // Sin vista que la retenga (fila cerrada entretanto o lectura fallida), vuelve ya.
            reserva?.Dispose();
        }
    }

    /// <summary>
    /// Bytes que ocupa en memoria construir la data URI de un PDF: el base64 y la URI
    /// con su prefijo conviven un momento (<see cref="ConstruirDataUriPdf"/>), a dos
    /// bytes por carácter.
    /// </summary>
    private static long BytesDataUriPdf(long bytesPdf) => 2 * (((bytesPdf + 2) / 3 * 4) + 64) * sizeof(char);

    private string? VistaPreviaDe(ItemLote item) =>
        _vistaPrevia is { } vista && vista.Clave == item.ClaveLocal ? vista.DataUri : null;

    private async Task ConfirmarAsync(ItemLote item)
    {
        if (_desechado || item.Confirmando)
            return;

        var carga = _cargaVigente;
        if (!Guid.TryParse(item.TrabajadorId, out var trabajadorId))
        {
            ToastService.Mostrar(Textos["TrabajadorFalta"], TonoToast.Error);
            return;
        }

        if (!Guid.TryParse(item.TipoDocumentoId, out var tipoDocumentoId))
        {
            ToastService.Mostrar(Textos["FaltaTipo"], TonoToast.Error);
            return;
        }

        // Sin valor por defecto: si no hay una fecha que la persona pueda ver
        // en el campo, no hay Documento.
        if (!DateOnly.TryParseExact(item.FechaEmision, "yyyy-MM-dd", out var fechaEmision))
        {
            ToastService.Mostrar(Textos["FaltaFecha"], TonoToast.Error);
            return;
        }

        if (fechaEmision > DiaDeNegocio.Hoy())
        {
            ToastService.Mostrar(Textos["FechaFutura"], TonoToast.Error);
            return;
        }

        item.Confirmando = true;
        StateHasChanged();

        try
        {
            await CrearDocumentoDelItemAsync(item, tipoDocumentoId, trabajadorId, fechaEmision, carga);
        }
        finally
        {
            if (EsVigente(carga))
                item.Confirmando = false;
        }
    }

    /// <summary>
    /// Único camino que crea el Documento, y solo lo alcanza
    /// <see cref="ConfirmarAsync"/>: el clic de una persona sobre un archivo
    /// concreto, con los valores que esa persona tenía a la vista.
    /// </summary>
    private async Task CrearDocumentoDelItemAsync(ItemLote item, Guid tipoDocumentoId, Guid trabajadorId, DateOnly fechaEmision, int carga)
    {
        if (item.Pdf is not { } pdf || _almacen is not { } almacen)
        {
            MarcarError(item, Textos["ErrorSinArchivo"]);
            return;
        }

        // Capacidad ANTES de almacenamiento, no al revés: comprobarlo aquí
        // evita el huérfano de mejor esfuerzo de DescartarArchivoHuerfanoAsync
        // en el único caso en que se puede evitar del todo — el mismo rol que
        // abre la pantalla es un rol que nunca podrá crear el Documento, así
        // que no hace falta escribir el PDF para descubrirlo.
        if (_esSoloLectura)
        {
            MarcarError(item, Textos["ErrorConsulta"]);
            return;
        }

        var tipo = _tiposDisponibles.FirstOrDefault(t => t.Id == tipoDocumentoId);
        DateOnly? fechaVencimientoManual = tipo is { AplicaVencimientoAutomatico: false }
            && DateOnly.TryParse(item.FechaVencimientoManual, out var fv)
                ? fv
                : null;
        var noCaduca = tipo is { AplicaVencimientoAutomatico: false } && item.NoCaduca;
        if (fechaVencimientoManual is not null && noCaduca)
        {
            MarcarError(item, TextosVigenciaDocumento.Texto("ErrorFechaYNoCaduca"));
            return;
        }

        // El blob se escribe antes que la fila, así que entre las dos
        // operaciones hay una ventana en la que el archivo existe sin
        // Documento que lo posea: sin fila, nada lo referencia, la retención
        // no lo alcanza y no hay forma fiable de purgarlo después. Se compensa
        // borrándolo en cuanto se sabe que el alta no salió adelante.
        //
        // La compensación cubre el fallo del comando, no la caída del proceso
        // entre ambos pasos: para eso hace falta staging durable con TTL y un
        // recolector, que es una decisión de arquitectura pendiente (ver el
        // informe del Módulo 2).
        // La vista previa de esta fila suelta su reserva del presupuesto de memoria antes
        // de guardar: si no, el guardado podría esperar a una reserva de su misma pantalla.
        if (_vistaPrevia?.Clave == item.ClaveLocal)
            SoltarVistaPrevia();

        string? archivoUrl = null;
        var conservarPdf = false;
        try
        {
            await using (var flujoPdf = almacen.AbrirLectura(pdf))
                archivoUrl = await AlmacenamientoArchivos.GuardarAsync(flujoPdf, "documento.pdf");

            var resultado = await Mediator.Send(new ConfirmarDocumentoPropuestoPorIaCommand(
                TrabajadorId: trabajadorId,
                TipoDocumentoId: tipoDocumentoId,
                FechaEmision: fechaEmision,
                FechaVencimientoManual: fechaVencimientoManual,
                ArchivoUrl: archivoUrl,
                Propuesta: PropuestaParaConfirmar(item),
                NoCaduca: noCaduca));

            if (resultado.EsFallido)
            {
                await DescartarArchivoHuerfanoAsync(archivoUrl);
                ToastService.MostrarError(resultado.Error);
                if (!EsVigente(carga)) return;
                MarcarError(item, resultado.Error.Mensaje);
                return;
            }

            ToastService.Mostrar(Textos["ToastCreado", item.NombreArchivo], TonoToast.Exito);
            if (!EsVigente(carga)) return;
            item.Estado = EstadoItem.Creado;
            item.DocumentoId = resultado.Valor;
            _totalCreados++;
        }
        catch (PresupuestoMemoriaAgotadoException)
        {
            // El almacenamiento no tuvo hueco en el presupuesto de memoria del proceso
            // para cifrarlo: no se llegó a escribir. Se descarta igual por si un paso
            // posterior lo lanzara algún día. Es un rechazo pasajero, no del archivo:
            // la fila sigue pendiente con su PDF para volver a confirmarla, sin repetir
            // la subida ni la lectura de la IA. Mismo mensaje que cualquier otro «no
            // cabe» de esta pantalla.
            await DescartarArchivoHuerfanoAsync(archivoUrl);
            ToastService.Mostrar(Textos["ErrorSinEspacio"], TonoToast.Error);
            conservarPdf = EsVigente(carga);
        }
        catch (Exception ex)
        {
            await DescartarArchivoHuerfanoAsync(archivoUrl);
            Logger.LogError(ex, "Fallo al crear el Documento de {ReferenciaArchivo} desde la subida múltiple.",
                ReferenciaArchivoTraza.De(item.NombreArchivo));
            ToastService.Mostrar(Textos["ErrorGuardar"], TonoToast.Error);
            if (!EsVigente(carga)) return;
            MarcarError(item, Textos["ErrorGuardar"]);
        }
        finally
        {
            // Tanto en éxito como en error, la pantalla no necesita seguir
            // reteniendo el PDF completo: su temporal se borra y su reserva vuelve.
            if (!conservarPdf)
                SoltarPdf(item);
        }
    }

    /// <summary>
    /// Borra un archivo ya guardado cuyo Documento no llegó a crearse. Es
    /// best-effort a propósito: si el borrado falla, el alta ya ha fallado y
    /// lo que procede es dejar constancia del huérfano, no enmascarar el
    /// error original con el de la compensación.
    /// </summary>
    private async Task DescartarArchivoHuerfanoAsync(string? archivoUrl)
    {
        if (archivoUrl is null) return;

        try
        {
            await AlmacenamientoArchivos.EliminarAsync(archivoUrl);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "Quedó un archivo huérfano en almacenamiento tras un alta fallida: {ArchivoUrl}. " +
                "No tiene Documento propietario, así que la retención no lo alcanza.",
                archivoUrl);
        }
    }

    private string? NombreTrabajadorElegido(ItemLote item) =>
        Guid.TryParse(item.TrabajadorId, out var id) ? _trabajadoresDisponibles.FirstOrDefault(t => t.Id == id)?.NombreCompleto : null;

    private string? NombreTipoElegido(ItemLote item) =>
        Guid.TryParse(item.TipoDocumentoId, out var id) ? _tiposDisponibles.FirstOrDefault(t => t.Id == id)?.Nombre : null;

    /// <summary>
    /// La línea de propuesta de la fila: lo que la persona tiene ahora en los controles, no la
    /// propuesta intacta; si ya difiere de ella, el rótulo lo dice para no presentar una
    /// corrección humana como si fuera lo que propuso la IA.
    /// </summary>
    private string ResumenPropuesta(ItemLote item)
    {
        string etiqueta = CorregidaPorPersona(item) ? Textos["PropuestaCorregida"] : Textos["PropuestaIntacta"];
        string sinDeterminar = Textos["SinDeterminar"];
        string emision = item.FechaEmision.Length > 0 ? FechaLegible(item.FechaEmision) : Textos["SinLeer"];
        string resumen = Textos["PropuestaResumen", etiqueta, NombreTrabajadorElegido(item) ?? sinDeterminar, NombreTipoElegido(item) ?? sinDeterminar, emision];
        if (RequiereVencimientoManual(item))
        {
            string vencimiento = item.FechaVencimientoManual.Length > 0 ? FechaLegible(item.FechaVencimientoManual) : Textos["SinIndicar"];
            resumen += Textos["PropuestaVencimiento", vencimiento];
        }

        return resumen;
    }

    private bool CorregidaPorPersona(ItemLote item)
    {
        var propuesta = item.Propuesta;
        Guid? trabajador = Guid.TryParse(item.TrabajadorId, out var t) ? t : null;
        Guid? tipo = Guid.TryParse(item.TipoDocumentoId, out var d) ? d : null;
        DateOnly? emision = DateOnly.TryParseExact(item.FechaEmision, "yyyy-MM-dd", out var e) ? e : null;
        DateOnly? vencimiento = RequiereVencimientoManual(item) && DateOnly.TryParse(item.FechaVencimientoManual, out var v) ? v : null;
        var vencimientoPropuesto = RequiereVencimientoManual(item) ? propuesta.FechaVencimiento : null;

        return trabajador != propuesta.TrabajadorId
            || tipo != propuesta.TipoDocumentoId
            || emision != propuesta.FechaEmision
            || vencimiento != vencimientoPropuesto
            // La IA nunca propone «no caduca»: marcarlo es siempre una corrección.
            || (RequiereVencimientoManual(item) && item.NoCaduca);
    }

    private static string FechaLegible(string fechaIso) =>
        DateOnly.TryParseExact(fechaIso, "yyyy-MM-dd", out var fecha) ? fecha.ToString("dd/MM/yyyy") : fechaIso;

    /// <summary>
    /// El vencimiento propuesto solo se compara con lo que la persona confirma
    /// cuando el tipo lo pide a mano; en un tipo de vencimiento automático el
    /// campo no existe y su ausencia no es una corrección.
    /// </summary>
    private PropuestaIaDocumento PropuestaParaConfirmar(ItemLote item) =>
        RequiereVencimientoManual(item) ? item.Propuesta : item.Propuesta with { FechaVencimiento = null };

    private bool RequiereVencimientoManual(ItemLote item) =>
        Guid.TryParse(item.TipoDocumentoId, out var tipoId)
        && _tiposDisponibles.FirstOrDefault(t => t.Id == tipoId) is { AplicaVencimientoAutomatico: false };

    /// <summary>
    /// El vencimiento que el alta va a calcular con la vigencia del tipo (la misma regla de
    /// <c>CalculadoraEstadoDocumento</c> que usa <c>CrearDocumentoCommand</c>), o nada si aún no hay
    /// tipo de vencimiento automático, fecha de emisión o vigencia con que calcularlo.
    /// </summary>
    private (DateOnly Fecha, string Texto)? VencimientoCalculado(ItemLote item)
    {
        if (!Guid.TryParse(item.TipoDocumentoId, out var tipoId)
            || _tiposDisponibles.FirstOrDefault(t => t.Id == tipoId) is not { AplicaVencimientoAutomatico: true } tipo
            || !DateOnly.TryParseExact(item.FechaEmision, "yyyy-MM-dd", out var emision)
            || tipo.VigenciaMeses is not { } meses
            || CalculadoraEstadoDocumento.CalcularFechaVencimiento(emision, meses) is not { } vencimiento)
            return null;

        return (vencimiento, Textos["VencimientoCalculado", vencimiento.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), meses]);
    }

    private string TextoVencimientoSinFecha(ItemLote item)
    {
        if (!Guid.TryParse(item.TipoDocumentoId, out var tipoId)
            || _tiposDisponibles.All(t => t.Id != tipoId))
            return Textos["VencimientoDependeDelTipo"];

        return DateOnly.TryParseExact(item.FechaEmision, "yyyy-MM-dd", out _)
            ? Textos["VencimientoSinVigencia"]
            : Textos["VencimientoSinEmision"];
    }

    private void Descartar(ItemLote item)
    {
        item.Estado = EstadoItem.Descartado;
        _totalDescartados++;
        SoltarPdf(item);
        SoltarMiniatura(item);
        item.Expandido = false;
    }

    /// <summary>La maqueta pide confirmar el descarte: el archivo se suelta de memoria y habría que volver a soltarlo.</summary>
    private Task ConfirmarDescarteAsync()
    {
        // Solo se descarta lo que sigue pendiente y no se está confirmando: si la confirmación ya está en vuelo,
        // descartar perdería el contenido del PDF y dejaría un Documento creado marcado como descartado.
        if (_paraDescartar is { Estado: EstadoItem.PendienteConfirmar, Confirmando: false } item)
        {
            Descartar(item);
            ToastService.Mostrar(Textos["ToastDescartado"], TonoToast.Exito);
        }

        _paraDescartar = null;
        return Task.CompletedTask;
    }

    private void QuitarDeLaLista(ItemLote item)
    {
        _items.Remove(item);
        SoltarPdf(item);
        SoltarMiniatura(item);
        ToastService.Mostrar(Textos["ToastQuitado"], TonoToast.Exito);
    }

    private void LimpiarResueltos()
    {
        foreach (var resuelto in _items.Where(i => i.Estado is EstadoItem.Creado or EstadoItem.Descartado))
        {
            SoltarPdf(resuelto);
            SoltarMiniatura(resuelto);
        }

        _items.RemoveAll(i => i.Estado is EstadoItem.Creado or EstadoItem.Descartado);
        ToastService.Mostrar(Textos["ToastLimpiado"], TonoToast.Exito);
    }

    private void AgregarItem(ItemLote item)
    {
        _items.Add(item);
        _totalRecibidos++;
        switch (item.Estado)
        {
            case EstadoItem.Creado:
                _totalCreados++;
                break;
            case EstadoItem.Descartado:
                _totalDescartados++;
                break;
            case EstadoItem.Error:
                _totalErrores++;
                break;
        }
    }

    private void MarcarError(ItemLote item, string mensaje)
    {
        item.Estado = EstadoItem.Error;
        item.MensajeError = mensaje;
        item.Archivo = null;
        _totalErrores++;
    }

    public void Dispose()
    {
        if (_desechado)
            return;

        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();
        // Borra todos los temporales de la pantalla y devuelve su reserva
        // (también la de las miniaturas): lo pendiente de confirmar se pierde
        // al salir, como ya avisa FS-14. La vista previa devuelve además su
        // reserva del presupuesto de memoria del proceso.
        SoltarVistaPrevia();
        _almacen?.Dispose();
    }

    private bool EsVigente(int carga) => !_desechado && carga == _cargaVigente;

    private string ObtenerEtiquetaEstado(ItemLote item) => item.Estado switch
    {
        EstadoItem.Cola => Textos["EstadoCola"],
        EstadoItem.Subiendo => Textos["EstadoSubiendo"],
        EstadoItem.Procesando => Textos["EstadoLeyendo"],
        EstadoItem.PendienteConfirmar => Textos["EstadoPendiente"],
        EstadoItem.Creado => Textos["EstadoCreado"],
        EstadoItem.Descartado => Textos["EstadoDescartado"],
        EstadoItem.Error => Textos["EstadoError"],
        _ => string.Empty
    };

    /// <summary>La línea gris de la fila: qué le pasa al archivo ahora, o de qué .zip salió.</summary>
    private string? DetalleItem(ItemLote item) => item.Estado switch
    {
        EstadoItem.Cola => Textos["DetalleCola"],
        EstadoItem.Subiendo => Textos["DetalleSubiendo"],
        EstadoItem.Procesando => Textos["DetalleLeyendo"],
        _ => item.Origen is { } origen ? Textos["DetalleOrigen", origen].Value : null
    };

    private static int PorcentajeSubida(ItemLote item) =>
        item.TamanoBytes <= 0 ? 100 : (int)Math.Min(100, item.BytesSubidos * 100 / item.TamanoBytes);

    /// <summary>Megabytes con una decimal y coma (la misma escritura en español y catalán), p. ej. «3,3».</summary>
    private static string Megas(long bytes) =>
        (bytes / 1048576d).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');

    private static TonoBadge ObtenerTonoEstado(EstadoItem estado) => estado switch
    {
        EstadoItem.Creado => TonoBadge.Exito,
        EstadoItem.PendienteConfirmar => TonoBadge.Advertencia,
        EstadoItem.Error => TonoBadge.Peligro,
        _ => TonoBadge.Neutro
    };

    private static string ConstruirDataUriPdf(byte[] contenidoPdf) =>
        $"data:application/pdf;base64,{Convert.ToBase64String(contenidoPdf)}";
}
