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
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.QuickGrid;
using Microsoft.Extensions.Logging;

namespace CaeManager.Web.Features.Documentos.Pages;

public partial class Documentos : ComponentBase
{
    private const long TamanoMaximoArchivoBytes = 10 * 1024 * 1024;
    private const int MaximoArchivosPorSubida = 20;

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

    private GridItemsProvider<DocumentoListaDto>? _proveedorElementos;

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

    protected override async Task OnInitializedAsync()
    {
        // Delegado estable — ver Clientes.razor.cs (bucle de recargas de QuickGrid).
        _proveedorElementos = ProveerElementosAsync;

        if (DocumentoId is not null)
            await AbrirEditarAsync(DocumentoId.Value);
        else if (TrabajadorId is not null && TipoDocumentoId is not null)
            await AbrirCrearParaFaltanteAsync(TrabajadorId.Value, TipoDocumentoId.Value);
        else if (EmpresaIdFaltante is not null && TipoDocumentoId is not null)
            await AbrirCrearParaFaltanteEmpresaAsync(EmpresaIdFaltante.Value, TipoDocumentoId.Value);
        else if (Accion == "crear")
            await AbrirCrearAsync();

        _filtrosGuardados = await Mediator.Send(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Documentos));
    }

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
    }

    private readonly PaginationState _paginacion = new() { ItemsPerPage = 20 };
    private QuickGrid<DocumentoListaDto>? _grid;

    private string _busqueda = string.Empty;
    private string _ambitoFiltro = string.Empty;
    private string _estadoFiltro = string.Empty;
    private bool _cargando = true;
    private bool _errorCarga;
    private int _totalElementos;

    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresDisponibles = [];
    private IReadOnlyList<ClienteSelectorDto> _clientesDisponibles = [];
    private IReadOnlyList<EmpresaSelectorDto> _empresasDisponibles = [];
    private IReadOnlyList<VehiculoSelectorDto> _vehiculosDisponibles = [];
    private IReadOnlyList<ProyectoSelectorDto> _proyectosDisponibles = [];
    private IReadOnlyList<TipoDocumentoListaDto> _tiposDisponibles = [];

    private bool _drawerVisible;
    private Guid? _editandoId;
    private string _ambitoAplicacion = nameof(AmbitoAplicacion.Trabajador);
    private string _trabajadorId = string.Empty;
    private string _clienteId = string.Empty;
    private string _empresaId = string.Empty;
    private string _vehiculoId = string.Empty;
    private string _proyectoId = string.Empty;
    private string _propietarioNombreSoloLectura = string.Empty;
    private string _tipoDocumentoId = string.Empty;
    private string _tipoDocumentoNombreSoloLectura = string.Empty;
    private bool _tipoDocumentoAplicaVencimientoAutomaticoEdit;
    private string _fechaEmision = string.Empty;
    private DateOnly? _fechaEmisionOriginal;
    private string _fechaVencimientoManual = string.Empty;
    private string? _glosarioDescripcion;
    private string? _glosarioCriteriosValidacion;
    private string? _glosarioSeSolicitaA;
    private string? _glosarioObservaciones;
    private string? _archivoUrl;
    private bool _subiendoArchivo;
    private bool _detectandoCampos;
    private string? _aliasSugerido;
    private Guid? _trabajadorIdParaAliasSugerido;
    private bool _asignandoAliasSugerido;
    private bool _confirmarTipoSospechosoVisible;
    private string _tipoSospechosoDetectadoNombre = string.Empty;
    private string _tipoSospechosoSeleccionadoNombre = string.Empty;
    private string _comentarios = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private bool _confirmarEliminarVisible;
    private Guid _idAEliminar;
    private string _propietarioAEliminar = string.Empty;
    private string _tipoDocumentoAEliminar = string.Empty;
    private bool _eliminando;

    private bool _confirmarVigenciaAnteriorVisible;
    private bool _procesandoConfirmacionVigencia;

    /// <summary>
    /// El alias (nombre con el que el trabajador firma o está dado de alta
    /// en plataformas externas) se incluye en el texto buscable para que
    /// escribirlo también lo encuentre — la identidad real para el
    /// pre-relleno automático siempre se verifica por DNI, nunca por este
    /// texto (ver DetectarCamposDocumentoQuery).
    /// </summary>
    private IReadOnlyList<OpcionBuscable> OpcionesTrabajadores => _trabajadoresDisponibles
        .Select(t => new OpcionBuscable(
            t.Id.ToString(),
            string.IsNullOrWhiteSpace(t.Alias) ? $"{t.NombreCompleto} ({t.Dni})" : $"{t.NombreCompleto} — {t.Alias} ({t.Dni})"))
        .ToList();

    /// <summary>
    /// Solo los tipos de documento sin vencimiento automático piden una
    /// fecha de vencimiento a mano — los automáticos la calculan siempre a
    /// partir de la vigencia en meses, así que no tiene sentido mostrarles
    /// el campo ni el botón de copiar.
    /// </summary>
    private bool RequiereVencimientoManual =>
        _editandoId is null
            ? _tiposDisponibles.FirstOrDefault(t => t.Id.ToString() == _tipoDocumentoId) is { AplicaVencimientoAutomatico: false }
            : !_tipoDocumentoAplicaVencimientoAutomaticoEdit;

    private bool TieneGlosario =>
        !string.IsNullOrWhiteSpace(_glosarioDescripcion)
        || !string.IsNullOrWhiteSpace(_glosarioCriteriosValidacion)
        || !string.IsNullOrWhiteSpace(_glosarioSeSolicitaA)
        || !string.IsNullOrWhiteSpace(_glosarioObservaciones);

    private async ValueTask<GridItemsProviderResult<DocumentoListaDto>> ProveerElementosAsync(
        GridItemsProviderRequest<DocumentoListaDto> request)
    {
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
                Descendente: descendente));

            _totalElementos = resultado.TotalElementos;

            var elementos = resultado.Elementos.ToList();
            _elementosPagina = elementos;
            _seleccionados.Clear();
            _idEnfocado = null;

            return GridItemsProviderResult.From(elementos, resultado.TotalElementos);
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
            _cargando = false;
            StateHasChanged();
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

    private async Task RecargarAsync()
    {
        await _paginacion.SetCurrentPageIndexAsync(0);

        if (_grid is not null)
            await _grid.RefreshDataAsync();

        StateHasChanged();
    }

    private async Task AbrirCrearAsync()
    {
        _ambitoAplicacion = nameof(AmbitoAplicacion.Trabajador);
        _trabajadoresDisponibles = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery());
        _tiposDisponibles = await Mediator.Send(new ObtenerTiposDocumentoQuery(AmbitoAplicacion: AmbitoAplicacion.Trabajador));

        _editandoId = null;
        _trabajadorId = string.Empty;
        _clienteId = string.Empty;
        _empresaId = string.Empty;
        _vehiculoId = string.Empty;
        _proyectoId = string.Empty;
        _tipoDocumentoId = string.Empty;
        _fechaEmision = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        _fechaEmisionOriginal = null;
        _fechaVencimientoManual = string.Empty;
        _archivoUrl = null;
        _comentarios = string.Empty;
        _glosarioDescripcion = null;
        _glosarioCriteriosValidacion = null;
        _glosarioSeSolicitaA = null;
        _glosarioObservaciones = null;
        _aliasSugerido = null;
        _trabajadorIdParaAliasSugerido = null;
        _confirmarTipoSospechosoVisible = false;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private async Task AbrirCrearParaFaltanteAsync(Guid trabajadorId, Guid tipoDocumentoId)
    {
        await AbrirCrearAsync();
        _trabajadorId = trabajadorId.ToString();
        CambiarTipoDocumento(tipoDocumentoId.ToString());
    }

    private async Task AbrirCrearParaFaltanteEmpresaAsync(Guid empresaId, Guid tipoDocumentoId)
    {
        await AbrirCrearAsync();
        await CambiarAmbitoAsync(nameof(AmbitoAplicacion.Empresa));
        _empresaId = empresaId.ToString();
        CambiarTipoDocumento(tipoDocumentoId.ToString());
    }

    private async Task CambiarAmbitoAsync(string valor)
    {
        _ambitoAplicacion = valor;
        _trabajadorId = string.Empty;
        _clienteId = string.Empty;
        _empresaId = string.Empty;
        _vehiculoId = string.Empty;
        _proyectoId = string.Empty;
        _aliasSugerido = null;
        _trabajadorIdParaAliasSugerido = null;

        var ambito = Enum.Parse<AmbitoAplicacion>(valor);

        if (ambito == AmbitoAplicacion.Cliente && _clientesDisponibles.Count == 0)
            _clientesDisponibles = await Mediator.Send(new ObtenerClientesParaSelectorQuery());
        else if (ambito == AmbitoAplicacion.Empresa && _empresasDisponibles.Count == 0)
            _empresasDisponibles = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery());
        else if (ambito == AmbitoAplicacion.Vehiculo && _vehiculosDisponibles.Count == 0)
            _vehiculosDisponibles = await Mediator.Send(new ObtenerVehiculosParaSelectorQuery());
        else if (ambito == AmbitoAplicacion.Proyecto && _proyectosDisponibles.Count == 0)
            _proyectosDisponibles = await Mediator.Send(new ObtenerProyectosParaSelectorQuery());

        _tiposDisponibles = await Mediator.Send(new ObtenerTiposDocumentoQuery(AmbitoAplicacion: ambito));
        CambiarTipoDocumento(string.Empty);
    }

    private void CambiarTipoDocumento(string valor)
    {
        _tipoDocumentoId = valor;

        var tipo = _tiposDisponibles.FirstOrDefault(t => t.Id.ToString() == valor);
        _glosarioDescripcion = tipo?.Descripcion;
        _glosarioCriteriosValidacion = tipo?.CriteriosValidacion;
        _glosarioSeSolicitaA = tipo?.SeSolicitaA;
        _glosarioObservaciones = tipo?.Observaciones;
    }

    private async Task AbrirEditarAsync(Guid id)
    {
        var documento = await Mediator.Send(new ObtenerDocumentoPorIdQuery(id));
        if (documento is null)
        {
            ToastService.Mostrar("No encontramos este documento. Puede que ya se haya eliminado.", TonoToast.Error);
            await RecargarAsync();
            return;
        }

        _editandoId = documento.Id;
        _ambitoAplicacion = documento.Ambito.ToString();
        _propietarioNombreSoloLectura = documento.PropietarioNombre;
        _tipoDocumentoNombreSoloLectura = documento.TipoDocumentoNombre;
        _tipoDocumentoAplicaVencimientoAutomaticoEdit = documento.TipoDocumentoAplicaVencimientoAutomatico;
        _fechaEmision = documento.FechaEmision.ToString("yyyy-MM-dd");
        _fechaEmisionOriginal = documento.FechaEmision;
        _fechaVencimientoManual = documento.FechaVencimiento?.ToString("yyyy-MM-dd") ?? string.Empty;
        _archivoUrl = documento.ArchivoUrl;
        _comentarios = documento.Comentarios ?? string.Empty;
        _glosarioDescripcion = documento.TipoDocumentoDescripcion;
        _glosarioCriteriosValidacion = documento.TipoDocumentoCriteriosValidacion;
        _glosarioSeSolicitaA = documento.TipoDocumentoSeSolicitaA;
        _glosarioObservaciones = documento.TipoDocumentoObservaciones;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        return Task.CompletedTask;
    }

    private void CopiarFechaEmisionAVencimiento() => _fechaVencimientoManual = _fechaEmision;

    /// <summary>Si el usuario cambia el trabajador a mano, la sugerencia de alias detectada para el anterior deja de tener sentido.</summary>
    private void CambiarTrabajadorSeleccionado(string valor)
    {
        _trabajadorId = valor;
        if (valor != _trabajadorIdParaAliasSugerido?.ToString())
            _aliasSugerido = null;
    }

    private async Task AceptarAliasSugeridoAsync()
    {
        if (_aliasSugerido is null || _trabajadorIdParaAliasSugerido is not { } trabajadorId
            || _trabajadorId != trabajadorId.ToString())
            return;

        _asignandoAliasSugerido = true;
        try
        {
            var resultado = await Mediator.Send(new AsignarAliasTrabajadorCommand(trabajadorId, _aliasSugerido));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Alias añadido al trabajador.", TonoToast.Exito);
            _aliasSugerido = null;
        }
        finally
        {
            _asignandoAliasSugerido = false;
        }
    }

    /// <summary>
    /// Acepta PDF, JPG, PNG y Word (.docx). Las imágenes y los Word se
    /// convierten a PDF automáticamente (Word vía LibreOffice headless, ver
    /// <see cref="ConversorArchivosPdf"/>); si se seleccionan varios archivos
    /// a la vez (p. ej. varias fotos de las páginas de un mismo documento),
    /// se combinan en un único PDF multipágina antes de guardarse — nunca se
    /// adjunta más de un archivo por Documento.
    /// </summary>
    private async Task ManejarArchivoSeleccionadoAsync(InputFileChangeEventArgs e)
    {
        var archivos = e.GetMultipleFiles(MaximoArchivosPorSubida);

        foreach (var archivo in archivos)
        {
            if (!ConversorArchivosPdf.EsPdf(archivo.Name)
                && !ConversorArchivosPdf.EsImagen(archivo.Name)
                && !ConversorArchivosPdf.EsWord(archivo.Name))
            {
                ToastService.Mostrar($"\"{archivo.Name}\" no es un PDF, JPG, PNG ni Word (.docx).", TonoToast.Error);
                return;
            }

            if (archivo.Size > TamanoMaximoArchivoBytes)
            {
                ToastService.Mostrar($"\"{archivo.Name}\" supera los 10 MB.", TonoToast.Error);
                return;
            }
        }

        _subiendoArchivo = true;
        StateHasChanged();

        try
        {
            var contenidos = new List<(byte[] Contenido, string NombreArchivo)>();
            foreach (var archivo in archivos)
            {
                await using var flujo = archivo.OpenReadStream(TamanoMaximoArchivoBytes);
                using var memoria = new MemoryStream();
                await flujo.CopyToAsync(memoria);
                contenidos.Add((memoria.ToArray(), archivo.Name));
            }

            // La comprobación de arriba solo mira el nombre — un archivo
            // renombrado a mano pasaría ese filtro. Esta mira los primeros
            // bytes del contenido real antes de convertirlo o guardarlo.
            foreach (var (contenido, nombreArchivo) in contenidos)
            {
                if (!ValidadorFirmaArchivo.TieneFirmaValida(contenido, nombreArchivo))
                {
                    ToastService.Mostrar(
                        $"\"{nombreArchivo}\" no es realmente un PDF, JPG, PNG ni Word (.docx) — su contenido no coincide con la extensión.",
                        TonoToast.Error);
                    return;
                }
            }

            var pdfUnificado = await ConversorArchivosPdf.UnificarAsync(contenidos, ConversorWordPdf);

            using var flujoPdf = new MemoryStream(pdfUnificado);
            _archivoUrl = await AlmacenamientoArchivos.GuardarAsync(flujoPdf, "documento.pdf");

            if (_editandoId is null && _ambitoAplicacion == nameof(AmbitoAplicacion.Trabajador))
                await DetectarCamposAsync(pdfUnificado);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Fallo al procesar el archivo subido en Documentos");
            ToastService.Mostrar("No pudimos procesar el archivo. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _subiendoArchivo = false;
        }
    }

    /// <summary>
    /// Mejor esfuerzo, mismo criterio que la detección de trabajadores/
    /// verificación IA de <see cref="CrearDocumentoCommand"/>: si la IA no
    /// está disponible o no encuentra una sugerencia fiable, el alta
    /// manual sigue funcionando exactamente igual que antes — nunca se
    /// bloquea ni se fuerza la selección, solo se ahorra el trabajo cuando
    /// hay una coincidencia clara. El usuario conserva ambos campos
    /// editables y puede corregir la sugerencia antes de guardar.
    /// </summary>
    private async Task DetectarCamposAsync(byte[] contenidoPdf)
    {
        _detectandoCampos = true;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(
                new DetectarCamposDocumentoQuery(contenidoPdf, "documento.pdf", AmbitoAplicacion.Trabajador));

            if (resultado.EsFallido)
                return;

            var deteccion = resultado.Valor;
            var huboSugerencia = false;

            if (deteccion.TipoDocumentoId is { } tipoDetectadoId
                && _tiposDisponibles.FirstOrDefault(t => t.Id == tipoDetectadoId) is { } tipoDetectadoDto)
            {
                if (string.IsNullOrEmpty(_tipoDocumentoId))
                {
                    CambiarTipoDocumento(tipoDetectadoId.ToString());
                    huboSugerencia = true;
                }
                else if (_tipoDocumentoId != tipoDetectadoId.ToString())
                {
                    // El usuario ya había elegido un tipo antes de subir el
                    // archivo — no se lo pisamos en silencio (huboSugerencia
                    // del alias/trabajador sí puede seguir, esto es aparte).
                    _tipoSospechosoDetectadoNombre = tipoDetectadoDto.Nombre;
                    _tipoSospechosoSeleccionadoNombre = _tiposDisponibles.FirstOrDefault(t => t.Id.ToString() == _tipoDocumentoId)?.Nombre ?? "el tipo seleccionado";
                    _confirmarTipoSospechosoVisible = true;
                }
            }

            if (deteccion.TrabajadorId is { } trabajadorDetectadoId
                && _trabajadoresDisponibles.Any(t => t.Id == trabajadorDetectadoId))
            {
                _trabajadorId = trabajadorDetectadoId.ToString();
                huboSugerencia = true;

                if (deteccion.AliasSugerido is not null)
                {
                    _aliasSugerido = deteccion.AliasSugerido;
                    _trabajadorIdParaAliasSugerido = trabajadorDetectadoId;
                }
            }

            if (huboSugerencia)
                ToastService.Mostrar("Detectamos automáticamente el trabajador y/o el tipo de documento — revisa antes de guardar.", TonoToast.Info);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "No se pudo completar la detección automática de campos al subir un Documento.");
        }
        finally
        {
            _detectandoCampos = false;
        }
    }

    /// <summary>
    /// Al renovar, si la nueva fecha de emisión es anterior a la que ya
    /// tenía el documento probablemente se subió el archivo equivocado —
    /// se pide confirmación explícita en vez de guardar directamente.
    /// </summary>
    private async Task GuardarAsync()
    {
        if (_editandoId is not null
            && _fechaEmisionOriginal is not null
            && DateOnly.TryParse(_fechaEmision, out var nuevaFecha)
            && nuevaFecha < _fechaEmisionOriginal)
        {
            _confirmarVigenciaAnteriorVisible = true;
            return;
        }

        await GuardarInternoAsync();
    }

    private async Task ConfirmarGuardarConVigenciaAnteriorAsync()
    {
        _procesandoConfirmacionVigencia = true;
        try
        {
            await GuardarInternoAsync();
        }
        finally
        {
            _procesandoConfirmacionVigencia = false;
            _confirmarVigenciaAnteriorVisible = false;
        }
    }

    private async Task GuardarInternoAsync()
    {
        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();

        try
        {
            if (!DateOnly.TryParse(_fechaEmision, out var fechaEmision))
            {
                _mensajeErrorFormulario = "Introduce una fecha de emisión válida.";
                return;
            }

            DateOnly? fechaVencimientoManual = RequiereVencimientoManual && DateOnly.TryParse(_fechaVencimientoManual, out var fv)
                ? fv
                : null;

            var comentarios = string.IsNullOrWhiteSpace(_comentarios) ? null : _comentarios;
            string? mensajeError;

            if (_editandoId is null)
            {
                var ambito = Enum.Parse<AmbitoAplicacion>(_ambitoAplicacion);
                var propietarioId = ambito switch
                {
                    AmbitoAplicacion.Trabajador => _trabajadorId,
                    AmbitoAplicacion.Cliente => _clienteId,
                    AmbitoAplicacion.Vehiculo => _vehiculoId,
                    AmbitoAplicacion.Proyecto => _proyectoId,
                    _ => _empresaId
                };

                if (!Guid.TryParse(propietarioId, out var idPropietario))
                {
                    _mensajeErrorFormulario = ambito switch
                    {
                        AmbitoAplicacion.Trabajador => "Selecciona un trabajador.",
                        AmbitoAplicacion.Cliente => "Selecciona un cliente.",
                        AmbitoAplicacion.Vehiculo => "Selecciona un vehículo.",
                        AmbitoAplicacion.Proyecto => "Selecciona un proyecto.",
                        _ => "Selecciona una empresa."
                    };
                    return;
                }

                if (!Guid.TryParse(_tipoDocumentoId, out var tipoDocumentoId))
                {
                    _mensajeErrorFormulario = "Selecciona un tipo de documento.";
                    return;
                }

                var resultado = await Mediator.Send(new CrearDocumentoCommand(
                    TrabajadorId: ambito == AmbitoAplicacion.Trabajador ? idPropietario : null,
                    ClienteId: ambito == AmbitoAplicacion.Cliente ? idPropietario : null,
                    EmpresaId: ambito == AmbitoAplicacion.Empresa ? idPropietario : null,
                    VehiculoId: ambito == AmbitoAplicacion.Vehiculo ? idPropietario : null,
                    ProyectoId: ambito == AmbitoAplicacion.Proyecto ? idPropietario : null,
                    TipoDocumentoId: tipoDocumentoId,
                    FechaEmision: fechaEmision,
                    FechaVencimientoManual: fechaVencimientoManual,
                    ArchivoUrl: _archivoUrl,
                    Comentarios: comentarios));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }
            else
            {
                var resultado = await Mediator.Send(
                    new RenovarDocumentoCommand(_editandoId.Value, fechaEmision, fechaVencimientoManual, _archivoUrl, comentarios));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }

            if (mensajeError is not null)
            {
                _mensajeErrorFormulario = mensajeError;
                return;
            }

            ToastService.Mostrar(
                _editandoId is null ? "Documento creado correctamente." : "Documento renovado correctamente.",
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

    private void AbrirEliminar(Guid id, string propietarioNombre, string tipoDocumentoNombre)
    {
        _idAEliminar = id;
        _propietarioAEliminar = propietarioNombre;
        _tipoDocumentoAEliminar = tipoDocumentoNombre;
        _confirmarEliminarVisible = true;
    }

    private async Task ConfirmarEliminarAsync()
    {
        _eliminando = true;

        try
        {
            var usuarioId = await CurrentUserService.ObtenerUsuarioActualIdAsync();
            var resultado = await Mediator.Send(new EliminarDocumentoCommand(_idAEliminar, usuarioId ?? Guid.Empty));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            }
            else
            {
                var idEliminado = _idAEliminar;
                ToastService.Mostrar("Documento eliminado correctamente.", TonoToast.Exito, "Deshacer", () => DeshacerEliminarAsync(idEliminado));
                _confirmarEliminarVisible = false;
                await RecargarAsync();
            }
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar el documento. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _eliminando = false;
        }
    }

    /// <summary>Fase D ("Deshacer al eliminar") — acción del toast tras eliminar, ver RestaurarDocumentoCommand.</summary>
    private async Task DeshacerEliminarAsync(Guid id)
    {
        var resultado = await Mediator.Send(new RestaurarDocumentoCommand(id));

        ToastService.Mostrar(
            resultado.EsExitoso ? "Documento restaurado." : resultado.Error.Mensaje,
            resultado.EsExitoso ? TonoToast.Exito : TonoToast.Error);

        if (resultado.EsExitoso)
            await RecargarAsync();
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
        _eliminandoLote = true;

        try
        {
            var usuarioId = await CurrentUserService.ObtenerUsuarioActualIdAsync();
            var resultado = await Mediator.Send(new EliminarDocumentosCommand(_seleccionados.ToList(), usuarioId ?? Guid.Empty));
            var dto = resultado.Valor;

            ToastService.Mostrar(
                dto.Errores.Count == 0
                    ? $"{dto.Eliminados} documento(s) eliminado(s)."
                    : $"{dto.Eliminados} eliminado(s). {dto.Errores.Count} no se pudieron borrar: {string.Join(" ", dto.Errores)}",
                dto.Errores.Count == 0 ? TonoToast.Exito : TonoToast.Advertencia);

            _seleccionados.Clear();
            _confirmarEliminarLoteVisible = false;
            await RecargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos eliminar los documentos seleccionados. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _eliminandoLote = false;
        }
    }

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
        await RecargarAsync();
    }

    private async Task GuardarFiltroActualAsync()
    {
        if (string.IsNullOrWhiteSpace(_nombreFiltroNuevo)) return;

        _guardandoFiltro = true;

        try
        {
            var valoresJson = JsonSerializer.Serialize(new FiltrosDocumentosJson(
                string.IsNullOrWhiteSpace(_busqueda) ? null : _busqueda,
                string.IsNullOrWhiteSpace(_ambitoFiltro) ? null : _ambitoFiltro,
                string.IsNullOrWhiteSpace(_estadoFiltro) ? null : _estadoFiltro));

            var resultado = await Mediator.Send(
                new GuardarFiltroCommand(PantallasConFiltrosGuardados.Documentos, _nombreFiltroNuevo, valoresJson));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            _filtrosGuardados = await Mediator.Send(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Documentos));
            _mostrarGuardarFiltro = false;
            _nombreFiltroNuevo = string.Empty;
            ToastService.Mostrar("Filtro guardado.", TonoToast.Exito);
        }
        finally
        {
            _guardandoFiltro = false;
        }
    }

    private async Task EliminarFiltroGuardadoAsync(Guid id)
    {
        var resultado = await Mediator.Send(new EliminarFiltroGuardadoCommand(id));
        if (resultado.EsFallido)
        {
            ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            return;
        }

        _filtrosGuardados = await Mediator.Send(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Documentos));
    }
}
