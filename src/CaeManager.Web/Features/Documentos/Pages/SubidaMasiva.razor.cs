using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Queries.DetectarCamposDocumento;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Documentos;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
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
/// que el alta individual) y se le ejecuta la detección de la Fase 54. Si
/// la IA resuelve Trabajador y Tipo con confianza alta (>= 95, mismo umbral
/// "verde" que Revisión IA) se crea el Documento sin pedir nada — si no,
/// queda en la lista de confirmación con lo que sí se haya detectado ya
/// prellenado, para que el usuario solo tenga que completar/corregir y
/// confirmar.
///
/// Visor: miniatura (primera página rasterizada a PNG con el mismo
/// <see cref="IRasterizadorPaginasPdfService"/> que ya usa el Caso Mixto de
/// IA documental) para poder reconocer cada archivo de un vistazo en la
/// lista sin abrir nada — cargar N PDFs completos a la vez sería
/// notablemente más lento. Al confirmar uno, el PDF completo se ve en un
/// &lt;iframe&gt; con el propio contenido en base64: los archivos pendientes
/// de confirmar deliberadamente no se guardan en <see cref="IFileStorageService"/>
/// todavía (evita huérfanos si se descartan, y evita abrir un endpoint que
/// sirva un archivo por clave sin un Documento que autorizar contra él —
/// el mismo vector IDOR que Fase 31/Issue #18 ya cerraron para el resto de
/// la aplicación) — solo se persiste el que el usuario confirma.
/// </summary>
public partial class SubidaMasiva : ComponentBase
{
    private const long TamanoMaximoArchivoBytes = 10 * 1024 * 1024;
    private const int MaximoArchivosPorLote = 60;
    private const int UmbralConfianzaAutoCreacion = 95;

    /// <summary>
    /// Bytes descomprimidos que puede llegar a retener el circuito por lote,
    /// contando lo que sale de los .zip y los archivos sueltos juntos.
    ///
    /// Es exactamente lo que este mismo lote ya podía ocupar sin comprimir
    /// (<see cref="MaximoArchivosPorLote"/> archivos al máximo por archivo),
    /// a propósito: así un .zip nunca da acceso a más memoria que subir los
    /// mismos archivos sueltos, que es lo único que la compresión estaba
    /// permitiendo saltarse. No baja ningún límite que hoy funcione, así que
    /// no rechaza ninguna subida que hoy se acepte.
    ///
    /// Acota el daño; no lo elimina. Sostener cientos de MB de PDFs en el
    /// estado de un circuito Blazor sigue siendo mucho, y la salida real es
    /// pasar el contenido a disco/objeto temporal y procesarlo fuera del
    /// proceso web en vez de en <c>byte[]</c> — pendiente de decisión, ver el
    /// informe del Módulo 2.
    /// </summary>
    private const long PresupuestoLoteBytes = MaximoArchivosPorLote * TamanoMaximoArchivoBytes;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private IFileStorageService AlmacenamientoArchivos { get; set; } = default!;
    [Inject] private IConversorWordPdfService ConversorWordPdf { get; set; } = default!;
    [Inject] private IRasterizadorPaginasPdfService Rasterizador { get; set; } = default!;
    [Inject] private ILogger<SubidaMasiva> Logger { get; set; } = default!;

    private enum EstadoItem { Procesando, PendienteConfirmar, Creado, Descartado, Error }

    private sealed class ItemLote
    {
        public Guid ClaveLocal { get; } = Guid.NewGuid();
        public required string NombreArchivo { get; init; }
        public byte[]? ContenidoPdf { get; set; }
        public string? MiniaturaBase64 { get; set; }
        public EstadoItem Estado { get; set; } = EstadoItem.Procesando;
        public string TrabajadorId { get; set; } = string.Empty;
        public string TipoDocumentoId { get; set; } = string.Empty;
        public string FechaEmision { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        public string FechaVencimientoManual { get; set; } = string.Empty;
        public int? Confianza { get; set; }
        public string? MensajeError { get; set; }
        public bool Expandido { get; set; }
        public bool Confirmando { get; set; }
    }

    private readonly List<ItemLote> _items = [];
    private bool _procesandoLote;
    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresDisponibles = [];
    private IReadOnlyList<TipoDocumentoListaDto> _tiposDisponibles = [];

    private IReadOnlyList<OpcionBuscable> OpcionesTrabajadores => _trabajadoresDisponibles
        .Select(t => new OpcionBuscable(
            t.Id.ToString(),
            string.IsNullOrWhiteSpace(t.Alias) ? $"{t.NombreCompleto} ({t.Dni})" : $"{t.NombreCompleto} — {t.Alias} ({t.Dni})"))
        .ToList();

    private int TotalCreados => _items.Count(i => i.Estado == EstadoItem.Creado);
    private int TotalPendientes => _items.Count(i => i.Estado == EstadoItem.PendienteConfirmar);
    private int TotalDescartados => _items.Count(i => i.Estado == EstadoItem.Descartado);
    private int TotalErrores => _items.Count(i => i.Estado == EstadoItem.Error);

    protected override async Task OnInitializedAsync()
    {
        _trabajadoresDisponibles = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery());
        _tiposDisponibles = await Mediator.Send(new ObtenerTiposDocumentoQuery(AmbitoAplicacion: AmbitoAplicacion.Trabajador));
    }

    private async Task ManejarArchivosSeleccionadosAsync(InputFileChangeEventArgs e)
    {
        var archivosSeleccionados = e.GetMultipleFiles(MaximoArchivosPorLote);

        _procesandoLote = true;
        StateHasChanged();

        try
        {
            var entradas = new List<(byte[] Contenido, string NombreArchivo)>();

            // Presupuesto compartido por todo el lote: cada archivo que entra
            // —suelto o salido de un .zip— descuenta de aquí. Sin esto, el
            // tope de 10 MB acotaba el .zip comprimido pero no lo que salía
            // de él (factor 1027 medido sobre ceros: 10 MB de entrada podían
            // volverse más de 10 GB en memoria del proceso web).
            var presupuestoRestante = PresupuestoLoteBytes;

            foreach (var archivo in archivosSeleccionados)
            {
                await using var flujo = archivo.OpenReadStream(TamanoMaximoArchivoBytes);
                using var memoria = new MemoryStream();
                await flujo.CopyToAsync(memoria);
                var contenido = memoria.ToArray();

                if (ExtractorZip.EsZip(archivo.Name))
                {
                    if (!ValidadorFirmaArchivo.TieneFirmaValida(contenido, archivo.Name))
                    {
                        _items.Add(NuevoItemError(archivo.Name, "El archivo no es realmente un .zip válido."));
                        continue;
                    }

                    IReadOnlyList<(byte[] Contenido, string NombreArchivo)> entradasZip;
                    try
                    {
                        entradasZip = ExtractorZip.Extraer(
                            contenido,
                            // Lo que quepa aún en el lote: un .zip no puede
                            // traer más archivos de los que faltan para el
                            // tope, y ese recuento ya no se hace después de
                            // haberlos expandido en memoria.
                            MaximoArchivosPorLote - entradas.Count,
                            TamanoMaximoArchivoBytes,
                            presupuestoRestante);
                    }
                    catch (InvalidDataException ex)
                    {
                        // Límite superado: es el caso previsto, no un fallo.
                        // El .zip se descarta entero y el lote continúa con
                        // el resto de archivos.
                        Logger.LogWarning(
                            "Se descartó un .zip que supera los límites de descompresión en la subida múltiple: {Motivo}",
                            ex.Message);
                        _items.Add(NuevoItemError(archivo.Name, ex.Message));
                        continue;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "No se pudo leer un .zip en la subida múltiple.");
                        _items.Add(NuevoItemError(archivo.Name, "No pudimos abrir este archivo .zip."));
                        continue;
                    }

                    presupuestoRestante -= entradasZip.Sum(e => (long)e.Contenido.Length);
                    entradas.AddRange(entradasZip);
                }
                else
                {
                    presupuestoRestante -= contenido.Length;
                    entradas.Add((contenido, archivo.Name));
                }

                if (entradas.Count > MaximoArchivosPorLote)
                {
                    ToastService.Mostrar(
                        $"El lote supera el máximo de {MaximoArchivosPorLote} archivos por subida.", TonoToast.Error);
                    return;
                }

                if (presupuestoRestante < 0)
                {
                    ToastService.Mostrar(
                        "El lote supera el tamaño máximo admitido para una subida.", TonoToast.Error);
                    return;
                }
            }

            foreach (var (contenido, nombreArchivo) in entradas)
                await ProcesarEntradaAsync(contenido, nombreArchivo);
        }
        finally
        {
            _procesandoLote = false;
            StateHasChanged();
        }
    }

    private async Task ProcesarEntradaAsync(byte[] contenido, string nombreArchivo)
    {
        if (!ConversorArchivosPdf.EsPdf(nombreArchivo) && !ConversorArchivosPdf.EsImagen(nombreArchivo) && !ConversorArchivosPdf.EsWord(nombreArchivo))
        {
            _items.Add(NuevoItemError(nombreArchivo, "No es un PDF, JPG, PNG ni Word (.docx) — se omitió."));
            StateHasChanged();
            return;
        }

        if (contenido.Length > TamanoMaximoArchivoBytes)
        {
            _items.Add(NuevoItemError(nombreArchivo, "Supera los 10 MB — se omitió."));
            StateHasChanged();
            return;
        }

        if (!ValidadorFirmaArchivo.TieneFirmaValida(contenido, nombreArchivo))
        {
            _items.Add(NuevoItemError(nombreArchivo, "El contenido real no coincide con la extensión — se omitió."));
            StateHasChanged();
            return;
        }

        var item = new ItemLote { NombreArchivo = nombreArchivo };
        _items.Add(item);
        StateHasChanged();

        byte[] contenidoPdf;
        try
        {
            contenidoPdf = await ConversorArchivosPdf.UnificarAsync([(contenido, nombreArchivo)], ConversorWordPdf);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "No se pudo convertir {ReferenciaArchivo} a PDF en la subida múltiple.",
                ReferenciaArchivoTraza.De(nombreArchivo));
            item.Estado = EstadoItem.Error;
            item.MensajeError = "No pudimos convertir este archivo a PDF.";
            StateHasChanged();
            return;
        }

        item.ContenidoPdf = contenidoPdf;
        item.MiniaturaBase64 = GenerarMiniatura(contenidoPdf, nombreArchivo);

        var deteccion = await DetectarCamposAsync(contenidoPdf, nombreArchivo);
        item.Confianza = deteccion?.ConfianzaGeneral;

        var tipoResuelto = deteccion?.TipoDocumentoId is { } tipoId && _tiposDisponibles.Any(t => t.Id == tipoId) ? tipoId : (Guid?)null;
        var trabajadorResuelto = deteccion?.TrabajadorId is { } trabId && _trabajadoresDisponibles.Any(t => t.Id == trabId) ? trabId : (Guid?)null;

        item.TipoDocumentoId = tipoResuelto?.ToString() ?? string.Empty;
        item.TrabajadorId = trabajadorResuelto?.ToString() ?? string.Empty;

        var confianzaSuficiente = deteccion?.ConfianzaGeneral >= UmbralConfianzaAutoCreacion;

        if (confianzaSuficiente && tipoResuelto is not null && trabajadorResuelto is not null)
            await CrearDocumentoDelItemAsync(item, tipoResuelto.Value, trabajadorResuelto.Value);
        else
            item.Estado = EstadoItem.PendienteConfirmar;

        // Feedback incremental: con un lote de varios archivos, esperar al
        // StateHasChanged de después del foreach entero dejaría la lista
        // congelada en "Procesando…" hasta que termina el último.
        StateHasChanged();
    }

    private static ItemLote NuevoItemError(string nombreArchivo, string mensaje) => new()
    {
        NombreArchivo = nombreArchivo,
        Estado = EstadoItem.Error,
        MensajeError = mensaje
    };

    /// <summary>Mejor esfuerzo — sin miniatura, el item sigue funcionando igual, solo sin imagen en la lista.</summary>
    private string? GenerarMiniatura(byte[] contenidoPdf, string nombreArchivo)
    {
        var resultado = Rasterizador.RasterizarPagina(contenidoPdf, 0);
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
    private async Task<DeteccionCamposDocumentoDto?> DetectarCamposAsync(byte[] contenidoPdf, string nombreArchivo)
    {
        try
        {
            var resultado = await Mediator.Send(new DetectarCamposDocumentoQuery(contenidoPdf, nombreArchivo, AmbitoAplicacion.Trabajador));
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
    /// de por sí) si el usuario despliega varias filas seguidas.
    /// </summary>
    private void AlternarExpandido(ItemLote item)
    {
        var expandir = !item.Expandido;
        foreach (var otro in _items) otro.Expandido = false;
        item.Expandido = expandir;
    }

    private async Task ConfirmarAsync(ItemLote item)
    {
        if (!Guid.TryParse(item.TrabajadorId, out var trabajadorId))
        {
            ToastService.Mostrar("Selecciona un trabajador antes de confirmar.", TonoToast.Error);
            return;
        }

        if (!Guid.TryParse(item.TipoDocumentoId, out var tipoDocumentoId))
        {
            ToastService.Mostrar("Selecciona un tipo de documento antes de confirmar.", TonoToast.Error);
            return;
        }

        item.Confirmando = true;
        StateHasChanged();

        try
        {
            await CrearDocumentoDelItemAsync(item, tipoDocumentoId, trabajadorId);
        }
        finally
        {
            item.Confirmando = false;
        }
    }

    private async Task CrearDocumentoDelItemAsync(ItemLote item, Guid tipoDocumentoId, Guid trabajadorId)
    {
        if (item.ContenidoPdf is null)
        {
            item.Estado = EstadoItem.Error;
            item.MensajeError = "No hay archivo que guardar.";
            return;
        }

        if (!DateOnly.TryParse(item.FechaEmision, out var fechaEmision))
            fechaEmision = DateOnly.FromDateTime(DateTime.UtcNow);

        var tipo = _tiposDisponibles.FirstOrDefault(t => t.Id == tipoDocumentoId);
        DateOnly? fechaVencimientoManual = tipo is { AplicaVencimientoAutomatico: false }
            && DateOnly.TryParse(item.FechaVencimientoManual, out var fv)
                ? fv
                : null;

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
        string? archivoUrl = null;
        try
        {
            using var flujoPdf = new MemoryStream(item.ContenidoPdf);
            archivoUrl = await AlmacenamientoArchivos.GuardarAsync(flujoPdf, "documento.pdf");

            var resultado = await Mediator.Send(new CrearDocumentoCommand(
                TrabajadorId: trabajadorId,
                ClienteId: null,
                EmpresaId: null,
                VehiculoId: null,
                ProyectoId: null,
                TipoDocumentoId: tipoDocumentoId,
                FechaEmision: fechaEmision,
                FechaVencimientoManual: fechaVencimientoManual,
                ArchivoUrl: archivoUrl,
                Comentarios: "Creado desde subida múltiple."));

            if (resultado.EsFallido)
            {
                await DescartarArchivoHuerfanoAsync(archivoUrl);
                item.Estado = EstadoItem.Error;
                item.MensajeError = resultado.Error.Mensaje;
                return;
            }

            item.Estado = EstadoItem.Creado;
        }
        catch (Exception ex)
        {
            await DescartarArchivoHuerfanoAsync(archivoUrl);
            Logger.LogError(ex, "Fallo al crear el Documento de {ReferenciaArchivo} desde la subida múltiple.",
                ReferenciaArchivoTraza.De(item.NombreArchivo));
            item.Estado = EstadoItem.Error;
            item.MensajeError = "No pudimos guardar este documento. Intenta nuevamente.";
        }
        finally
        {
            // Tanto en éxito como en error, el circuito no necesita seguir
            // reteniendo el buffer del PDF completo.
            item.ContenidoPdf = null;
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

    private bool RequiereVencimientoManual(ItemLote item) =>
        Guid.TryParse(item.TipoDocumentoId, out var tipoId)
        && _tiposDisponibles.FirstOrDefault(t => t.Id == tipoId) is { AplicaVencimientoAutomatico: false };

    private void Descartar(ItemLote item)
    {
        item.Estado = EstadoItem.Descartado;
        item.ContenidoPdf = null;
        item.MiniaturaBase64 = null;
    }

    private void QuitarDeLaLista(ItemLote item) => _items.Remove(item);

    private void LimpiarResueltos() => _items.RemoveAll(i => i.Estado is EstadoItem.Creado or EstadoItem.Descartado);

    private static string ObtenerEtiquetaEstado(EstadoItem estado) => estado switch
    {
        EstadoItem.Procesando => "Procesando…",
        EstadoItem.PendienteConfirmar => "Pendiente de confirmar",
        EstadoItem.Creado => "Creado",
        EstadoItem.Descartado => "Descartado",
        EstadoItem.Error => "Error",
        _ => string.Empty
    };

    private static TonoBadge ObtenerTonoEstado(EstadoItem estado) => estado switch
    {
        EstadoItem.Creado => TonoBadge.Exito,
        EstadoItem.PendienteConfirmar => TonoBadge.Advertencia,
        EstadoItem.Error => TonoBadge.Peligro,
        _ => TonoBadge.Neutro
    };

    private static TonoBadge TonoConfianza(int confianza) => confianza switch
    {
        >= 95 => TonoBadge.Exito,
        >= 70 => TonoBadge.Advertencia,
        _ => TonoBadge.Peligro
    };

    private static string ConstruirDataUriPdf(byte[] contenidoPdf) =>
        $"data:application/pdf;base64,{Convert.ToBase64String(contenidoPdf)}";
}
