using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Plantillas.Commands.ConfirmarPlantillaDocumentoVersion;
using CaeManager.Application.Plantillas.Commands.CrearPlantillaDocumento;
using CaeManager.Application.Plantillas.Commands.GenerarDocumentoIndividual;
using CaeManager.Application.Plantillas.Commands.GuardarElementosPlantilla;
using CaeManager.Application.Plantillas.Commands.IniciarLoteGeneracionDocumentos;
using CaeManager.Application.Plantillas.Commands.ProcesarItemLoteGeneracion;
using CaeManager.Application.Plantillas.Queries.DetectarCamposPlantilla;
using CaeManager.Application.Plantillas.Queries.ObtenerLoteGeneracionDocumentos;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillaDocumentoVersion;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PdfSharp.Pdf.IO;

namespace CaeManager.Web.Features.Plantillas.Pages;

public partial class ConfigurarPlantilla : ComponentBase, IAsyncDisposable
{
    private const long TamanoMaximoArchivoBytes = 10 * 1024 * 1024;
    private const double AnchoPorDefectoCampo = 150;
    private const double AltoPorDefectoCampo = 22;

    [Parameter] public Guid? PlantillaDocumentoVersionId { get; set; }

    private readonly string _idContenedor = $"editor-plantilla-{Guid.NewGuid():N}";
    private DotNetObjectReference<ConfigurarPlantilla>? _referencia;
    private IJSObjectReference? _modulo;
    private bool _moduloIniciado;

    private sealed class PaginaEditor
    {
        public required int NumeroPagina { get; init; }
        public required string ImagenBase64 { get; init; }
        public required double AnchoPuntos { get; init; }
        public required double AltoPuntos { get; init; }
    }

    private sealed class ElementoEditor
    {
        public required int IdLocal { get; init; }

        /// <summary>Id real de <c>PlantillaElemento</c> — null para un elemento añadido en el editor que aún no se ha guardado. Necesario para <see cref="GenerarDocumentoIndividualCommand.ValoresManuales"/>.</summary>
        public Guid? IdReal { get; init; }
        public TipoElementoPlantilla Tipo { get; set; }
        public int Pagina { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Ancho { get; set; }
        public double Alto { get; set; }
        public string EtiquetaVisible { get; set; } = string.Empty;
        public FuenteDatoPlantilla? FuenteDato { get; set; }
        public string? ValorConstante { get; set; }
        public string? Formato { get; set; }
        public bool Obligatorio { get; set; }
        public RolFirmantePlantilla? RolFirmante { get; set; }
        public string? NombreCampoAcroForm { get; set; }
    }

    // Formulario de alta — solo en modo creación (sin PlantillaDocumentoVersionId en la ruta).
    private string _nombre = string.Empty;
    private string? _descripcion;
    private AmbitoAplicacion _ambitoAplicacion = AmbitoAplicacion.Trabajador;
    private FormatoOrigenPlantilla _formatoOrigenSeleccionado = FormatoOrigenPlantilla.PdfConCampos;
    private Guid? _centroId;
    private Guid? _clienteId;
    private Guid? _tipoDocumentoId;
    private byte[]? _archivoSeleccionado;
    private string? _nombreArchivoSeleccionado;
    private bool _creando;
    private IReadOnlyList<CentroSelectorDto> _centrosDisponibles = [];
    private IReadOnlyList<ClienteSelectorDto> _clientesDisponibles = [];
    private IReadOnlyList<TipoDocumentoListaDto> _tiposDocumentoDisponibles = [];

    // Editor — una vez creada (o al reabrir un borrador existente).
    private Guid? _versionIdActual;
    private Guid _documentoId;
    private string _nombrePlantilla = string.Empty;
    private FormatoOrigenPlantilla _formatoOrigen;
    private EstadoConfiguracionPlantilla _estadoConfiguracion;
    private List<PaginaEditor> _paginas = [];
    private List<ElementoEditor> _elementos = [];
    private int _siguienteIdLocal = 1;
    private int? _idLocalSeleccionado;
    private bool _cargandoEditor;
    private bool _guardando;
    private bool _confirmando;
    private bool _errorCarga;

    /// <summary>
    /// Versión que la ruta pidió cargar. Se fija ANTES del primer await de la
    /// carga —a diferencia de <see cref="_versionIdActual"/>, que solo existe
    /// cuando el editor ya está montado—: así un repintado con la misma ruta
    /// mientras la carga está en vuelo no dispara una segunda carga, y
    /// «Reintentar» sabe qué volver a pedir tras un fallo.
    /// </summary>
    private Guid? _versionSolicitada;

    /// <summary>
    /// Número de la última carga del editor. Se captura ANTES del
    /// <c>await</c> y, al volver, solo se escribe estado si sigue siendo la
    /// vigente y la página sigue viva: una carga superada (cambio de versión en
    /// la ruta, «Reintentar», alta recién creada) no pisa a la actual. Mismo
    /// patrón que PlantillasTab y Empresas.
    /// </summary>
    private int _cargaVigente;

    /// <summary>
    /// Se cancela al retirarse la página: la consulta, la lectura del PDF o el
    /// guardado en curso dejan de trabajar para nadie. Mismo patrón que
    /// PlantillasTab.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    /// <summary>Campo cuya eliminación espera confirmación (diálogo «Eliminar este campo»).</summary>
    private int? _idLocalPendienteEliminar;

    /// <summary>Diálogo «Confirmar la plantilla»: confirmar es irreversible (ADR-010 § 2.3).</summary>
    private bool _dialogoConfirmarVisible;

    // Públicas, no internas: CaeManager.Web no tiene InternalsVisibleTo — mismo
    // motivo que ComprobarRecuentoDePaginas (ver su doc-comment).
    public const string TextoDescripcionEditable =
        "Coloca cada campo sobre el PDF y di de dónde sale su dato. Mientras no la confirmes, esta versión no genera documentos.";

    public const string TextoDescripcionConfirmada =
        "Versión confirmada e inmutable: es la fuente de verdad determinista para generar. Para cambiar algo, se crea una versión nueva.";

    /// <summary>El alta (/plantillas/nueva) mientras todavía no se ha creado la versión.</summary>
    private bool EsAlta => PlantillaDocumentoVersionId is null && _versionIdActual is null;

    /// <summary>Hay una versión montada en el editor (ni alta, ni cargando, ni en error).</summary>
    private bool EnEditor => !EsAlta && _versionIdActual is not null && !_cargandoEditor && !_errorCarga;

    private string TituloPagina => EsAlta
        ? "Nueva plantilla"
        : string.IsNullOrWhiteSpace(_nombrePlantilla) ? "Plantilla" : _nombrePlantilla;

    private string TextoPaginas => _paginas.Count == 1 ? "1 página" : $"{_paginas.Count} páginas";

    /// <summary>Antes el badge pintaba el nombre del enum: «PendienteRevision». Mismos rótulos que el catálogo.</summary>
    private static string TextoEstado(EstadoConfiguracionPlantilla estado) => estado switch
    {
        EstadoConfiguracionPlantilla.Borrador => "Borrador",
        EstadoConfiguracionPlantilla.PendienteRevision => "Pendiente de revisión",
        EstadoConfiguracionPlantilla.Confirmada => "Confirmada",
        _ => estado.ToString()
    };

    /// <summary>Mismos rótulos que el catálogo (PlantillasTab): el enum no lleva tildes.</summary>
    private static string TextoAmbito(AmbitoAplicacion ambito) => ambito switch
    {
        AmbitoAplicacion.Vehiculo => "Vehículo",
        _ => ambito.ToString()
    };

    private static string TextoFormato(FormatoOrigenPlantilla formato) => formato switch
    {
        FormatoOrigenPlantilla.PdfConCampos => "PDF con campos (AcroForm)",
        FormatoOrigenPlantilla.PdfVisual => "PDF visual (posición a mano)",
        _ => formato.ToString()
    };

    /// <summary>Mismos rótulos que el selector «Tipo» del panel.</summary>
    private static string TextoTipo(TipoElementoPlantilla tipo) => tipo switch
    {
        TipoElementoPlantilla.Checkbox => "Casilla",
        TipoElementoPlantilla.Constante => "Texto fijo",
        _ => tipo.ToString()
    };

    private static string TextoAccesibleCaja(ElementoEditor elemento) =>
        $"Campo {elemento.EtiquetaVisible}, tipo {TextoTipo(elemento.Tipo)}{(elemento.Obligatorio ? ", obligatorio" : string.Empty)}";

    /// <summary>La respuesta es de la carga vigente y la página sigue viva.</summary>
    private bool EsVigente(int carga) => !_desechado && carga == _cargaVigente;

    // Generación individual (PR7) — solo cuando la versión está Confirmada.
    private Guid? _ownerIdGeneracion;
    private Guid? _centroIdGeneracion;
    private readonly Dictionary<Guid, string> _valoresManualesPorIdReal = [];
    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresDisponibles = [];
    private IReadOnlyList<EmpresaSelectorDto> _empresasDisponibles = [];
    private bool _opcionesGeneracionCargadas;
    private bool _generando;
    private Guid? _documentoGeneradoId;

    /// <summary>DEC-5 (2026-09-02): los obligatorios que resolvieron vacíos en la última generación individual. El toast se va solo; esto se queda junto al enlace al PDF.</summary>
    private IReadOnlyList<string> _camposObligatoriosVacios = [];

    /// <summary>DEC-32 (REC-115): valores presentes que el campo (radio o checkbox) no reconoció en la última generación individual — misma idea que <see cref="_camposObligatoriosVacios"/>, categoría distinta.</summary>
    private IReadOnlyList<AvisoValorNoReconocidoDto> _valoresNoReconocidos = [];

    // Generación en lote (PR8) — solo AmbitoAplicacion.Trabajador (ADR-010 § 3).
    private sealed class ItemLoteEstado
    {
        public required Guid ItemId { get; init; }
        public required Guid TrabajadorId { get; init; }
        public required string TrabajadorNombre { get; init; }
        public EstadoItemGeneracion Estado { get; set; } = EstadoItemGeneracion.Pendiente;
        public string? Error { get; set; }
    }

    private bool _modoLote;
    private readonly HashSet<Guid> _trabajadoresSeleccionadosLote = [];
    private List<ItemLoteEstado> _itemsLote = [];
    private bool _procesandoLote;
    private int TotalCompletadosLote => _itemsLote.Count(i => i.Estado == EstadoItemGeneracion.Completado);
    private int TotalConAvisosLote => _itemsLote.Count(i => i.Estado == EstadoItemGeneracion.CompletadoConAvisos);
    private int TotalFallidosLote => _itemsLote.Count(i => i.Estado == EstadoItemGeneracion.Fallido);

    /// <summary>Cuenta los tres estados terminales: contar solo completados y fallidos dejaba el progreso corto en cuanto un ítem salía con avisos.</summary>
    private int TotalProcesadosLote => _itemsLote.Count(i => i.Estado != EstadoItemGeneracion.Pendiente);

    private ElementoEditor? ElementoSeleccionado =>
        _idLocalSeleccionado is { } id ? _elementos.FirstOrDefault(e => e.IdLocal == id) : null;

    protected override async Task OnInitializedAsync()
    {
        _centrosDisponibles = await Mediator.Send(new ObtenerCentrosParaSelectorQuery());
        _clientesDisponibles = await Mediator.Send(new ObtenerClientesParaSelectorQuery());
        await CargarTiposDocumentoAsync();
    }

    private async Task CargarTiposDocumentoAsync()
    {
        _tiposDocumentoDisponibles = await Mediator.Send(new ObtenerTiposDocumentoQuery(AmbitoAplicacion: _ambitoAplicacion));
        if (_tipoDocumentoId is { } id && _tiposDocumentoDisponibles.All(t => t.Id != id))
            _tipoDocumentoId = null;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (PlantillaDocumentoVersionId == _versionSolicitada) return;

        if (PlantillaDocumentoVersionId is not { } versionId)
        {
            // Vuelta al alta con la misma instancia: la carga que hubiera en
            // vuelo deja de ser la vigente y no puede montar el editor encima.
            _versionSolicitada = null;
            _versionIdActual = null;
            _cargaVigente++;
            _cargandoEditor = false;
            _errorCarga = false;
            return;
        }

        await CargarVersionExistenteAsync(versionId);
    }

    private Task ReintentarAsync() =>
        _versionSolicitada is { } versionId ? CargarVersionExistenteAsync(versionId) : Task.CompletedTask;

    private async Task CargarVersionExistenteAsync(Guid versionId)
    {
        if (_desechado)
            return;

        var carga = ++_cargaVigente;
        _versionSolicitada = versionId;
        _cargandoEditor = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ObtenerPlantillaDocumentoVersionQuery(versionId), _ciclo.Token);
            if (!EsVigente(carga))
                return;

            if (resultado.EsFallido)
            {
                _errorCarga = true;
                return;
            }

            var detalle = resultado.Valor;

            // No pasa por IRegistroAccesoDocumentoSensibleService (DEC-36,
            // HO-099-01 § 6-7): esto es el PDF en blanco de PlantillaDocumento
            // ("formulario reutilizable a partir del cual se generan
            // documentos"), que un Administrador configura — no un Documento
            // relleno de una persona concreta.
            await using var flujo = await AlmacenamientoArchivos.AbrirAsync(detalle.ArchivoOriginalUrl, _ciclo.Token);
            using var memoria = new MemoryStream();
            await flujo.CopyToAsync(memoria, _ciclo.Token);
            if (!EsVigente(carga))
                return;

            var contenido = memoria.ToArray();

            await IniciarEditorAsync(
                carga, versionId, detalle.PlantillaDocumentoId, detalle.NombrePlantilla, detalle.AmbitoAplicacion, detalle.FormatoOrigen,
                detalle.EstadoConfiguracion, contenido, ElementosIniciales(detalle));
        }
        catch (Exception) when (!EsVigente(carga))
        {
            // Una carga superada (o cancelada al retirarse la página) que falla
            // no es un error de la vigente: no puede tapar su resultado.
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            if (EsVigente(carga))
            {
                _cargandoEditor = false;
                StateHasChanged();
            }
        }
    }

    private List<ElementoEditor> ElementosIniciales(PlantillaDocumentoVersionDetalleDto detalle) =>
        detalle.Elementos.Select(e => new ElementoEditor
        {
            IdLocal = _siguienteIdLocal++,
            IdReal = e.Id,
            Tipo = e.Tipo,
            Pagina = e.Pagina,
            X = e.X,
            Y = e.Y,
            Ancho = e.Ancho,
            Alto = e.Alto,
            EtiquetaVisible = e.EtiquetaVisible,
            FuenteDato = e.FuenteDato,
            ValorConstante = e.ValorConstante,
            Formato = e.Formato,
            Obligatorio = e.Obligatorio,
            RolFirmante = e.RolFirmante,
            NombreCampoAcroForm = e.NombreCampoAcroForm,
        }).ToList();

    private async Task ManejarArchivoSeleccionadoAsync(InputFileChangeEventArgs e)
    {
        await using var flujo = e.File.OpenReadStream(TamanoMaximoArchivoBytes);
        using var memoria = new MemoryStream();
        await flujo.CopyToAsync(memoria);
        _archivoSeleccionado = memoria.ToArray();
        _nombreArchivoSeleccionado = e.File.Name;
    }

    private bool PuedeCrear =>
        !_creando && !string.IsNullOrWhiteSpace(_nombre) && _archivoSeleccionado is { Length: > 0 } && _tipoDocumentoId is not null;

    private async Task CrearPlantillaAsync()
    {
        if (!PuedeCrear || _archivoSeleccionado is null || _tipoDocumentoId is not { } tipoDocumentoId) return;

        _creando = true;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new CrearPlantillaDocumentoCommand(
                _nombre, _ambitoAplicacion, _formatoOrigenSeleccionado, tipoDocumentoId, _archivoSeleccionado,
                _nombreArchivoSeleccionado ?? "plantilla.pdf", _descripcion, _centroId, _clienteId));

            if (resultado.EsFallido)
            {
                Toasts.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            // La ruta pasará a /plantillas/{id}/editar (NavigateTo de abajo):
            // marcarla ya como solicitada evita que OnParametersSetAsync la
            // vuelva a cargar desde el servidor sobre el editor recién montado.
            var carga = ++_cargaVigente;
            _versionSolicitada = resultado.Valor.PlantillaDocumentoVersionId;
            await IniciarEditorAsync(
                carga, resultado.Valor.PlantillaDocumentoVersionId, resultado.Valor.PlantillaDocumentoId, _nombre, _ambitoAplicacion,
                _formatoOrigenSeleccionado, EstadoConfiguracionPlantilla.Borrador, _archivoSeleccionado, []);

            Navigation.NavigateTo($"/plantillas/{resultado.Valor.PlantillaDocumentoVersionId}/editar", replace: true);
        }
        finally
        {
            _creando = false;
        }
    }

    /// <summary>
    /// Rasteriza cada página (fondo del editor) y, si la versión todavía no
    /// tiene ningún elemento, ejecuta la detección inicial (PR4) y la
    /// persiste de inmediato — es "la IA propone", el guardado explícito
    /// que sigue (botón "Guardar cambios"/"Confirmar") es "el humano revisa
    /// y confirma" (ADR-010 § 2.4).
    /// </summary>
    private async Task IniciarEditorAsync(
        int carga, Guid versionId, Guid documentoId, string nombrePlantilla, AmbitoAplicacion ambitoAplicacion, FormatoOrigenPlantilla formatoOrigen,
        EstadoConfiguracionPlantilla estadoConfiguracion, byte[] contenidoPdf, List<ElementoEditor> elementosExistentes)
    {
        if (!EsVigente(carga))
            return;

        _versionIdActual = versionId;
        _documentoId = documentoId;
        _nombrePlantilla = nombrePlantilla;
        _ambitoAplicacion = ambitoAplicacion;
        _formatoOrigen = formatoOrigen;
        _estadoConfiguracion = estadoConfiguracion;
        _elementos = elementosExistentes;
        _idLocalSeleccionado = null;

        _paginas = await RasterizarPaginasAsync(contenidoPdf);

        if (_elementos.Count == 0 && estadoConfiguracion == EstadoConfiguracionPlantilla.Borrador)
            await EjecutarDeteccionInicialAsync(carga, formatoOrigen, contenidoPdf);

        if (estadoConfiguracion == EstadoConfiguracionPlantilla.Confirmada && EsVigente(carga))
            await CargarOpcionesGeneracionAsync();
    }

    private async Task CargarOpcionesGeneracionAsync()
    {
        if (_opcionesGeneracionCargadas) return;
        _opcionesGeneracionCargadas = true;

        switch (_ambitoAplicacion)
        {
            case AmbitoAplicacion.Trabajador:
                _trabajadoresDisponibles = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery());
                break;
            case AmbitoAplicacion.Empresa:
                _empresasDisponibles = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery());
                break;
                // Cliente reutiliza _clientesDisponibles, ya cargado en OnInitializedAsync.
        }
    }

    private bool PuedeGenerar => !_generando && _ownerIdGeneracion is not null;

    private async Task GenerarDocumentoAsync()
    {
        if (!PuedeGenerar || _versionIdActual is not { } versionId || _ownerIdGeneracion is not { } ownerId) return;

        _generando = true;
        _documentoGeneradoId = null;
        _camposObligatoriosVacios = [];
        _valoresNoReconocidos = [];
        StateHasChanged();

        try
        {
            var valoresManuales = _valoresManualesPorIdReal
                .Where(par => !string.IsNullOrWhiteSpace(par.Value))
                .ToDictionary(par => par.Key, par => par.Value);

            var resultado = await Mediator.Send(new GenerarDocumentoIndividualCommand(
                versionId, ownerId, _centroIdGeneracion, valoresManuales.Count == 0 ? null : valoresManuales));

            if (resultado.EsFallido)
            {
                Toasts.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            _documentoGeneradoId = resultado.Valor.DocumentoId;
            _camposObligatoriosVacios = resultado.Valor.CamposObligatoriosVacios;
            _valoresNoReconocidos = resultado.Valor.ValoresNoReconocidos;

            // DEC-5 (propietario, 2026-09-02): se genera igual, pero con aviso
            // visible — bloquear rompería lotes enteros por un campo. DEC-32
            // (REC-115) añade la segunda categoría: un valor que el campo no
            // reconoció, distinta de un obligatorio vacío.
            if (_camposObligatoriosVacios.Count > 0 || _valoresNoReconocidos.Count > 0)
            {
                var avisos = new List<string>();
                if (_camposObligatoriosVacios.Count > 0)
                    avisos.Add($"sin dato en: {string.Join(", ", _camposObligatoriosVacios)}");
                if (_valoresNoReconocidos.Count > 0)
                    avisos.Add($"valores no reconocidos en: {string.Join(", ", _valoresNoReconocidos.Select(a => a.EtiquetaCampo))}");

                Toasts.Mostrar($"Documento generado, pero {string.Join("; ", avisos)}.", TonoToast.Advertencia);
            }
            else
                Toasts.Mostrar("Documento generado.", TonoToast.Exito);
        }
        finally
        {
            _generando = false;
        }
    }

    private void CambiarValorManual(Guid idElemento, string valor) => _valoresManualesPorIdReal[idElemento] = valor;

    private void AlternarTrabajadorLote(Guid trabajadorId, bool marcado)
    {
        if (marcado) _trabajadoresSeleccionadosLote.Add(trabajadorId);
        else _trabajadoresSeleccionadosLote.Remove(trabajadorId);
    }

    private bool PuedeGenerarLote => !_procesandoLote && _trabajadoresSeleccionadosLote.Count > 0;

    /// <summary>
    /// IniciarLoteGeneracionDocumentosCommand crea el lote y sus items de una
    /// vez (ya consultables por Query aunque la página se recargue); el
    /// bucle que sigue —ProcesarItemLoteGeneracionCommand uno a uno— es la
    /// ejecución síncrona-inmediata que ADR-010 § 2.6 nombra como opción por
    /// defecto del MVP, con progreso en vivo dentro de este mismo circuito.
    /// </summary>
    private async Task GenerarLoteAsync()
    {
        if (!PuedeGenerarLote || _versionIdActual is not { } versionId) return;

        _procesandoLote = true;
        _itemsLote = [];
        StateHasChanged();

        try
        {
            var valoresManuales = _valoresManualesPorIdReal
                .Where(par => !string.IsNullOrWhiteSpace(par.Value))
                .ToDictionary(par => par.Key, par => par.Value);

            var trabajadorIds = _trabajadoresSeleccionadosLote.ToList();
            var resultado = await Mediator.Send(new IniciarLoteGeneracionDocumentosCommand(
                versionId, trabajadorIds, _centroIdGeneracion, valoresManuales.Count == 0 ? null : valoresManuales));

            if (resultado.EsFallido)
            {
                Toasts.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            _itemsLote = resultado.Valor.ItemIds.Select((itemId, indice) => new ItemLoteEstado
            {
                ItemId = itemId,
                TrabajadorId = trabajadorIds[indice],
                TrabajadorNombre = _trabajadoresDisponibles.FirstOrDefault(t => t.Id == trabajadorIds[indice])?.NombreCompleto ?? "—",
            }).ToList();
            StateHasChanged();

            foreach (var item in _itemsLote)
            {
                var resultadoItem = await Mediator.Send(new ProcesarItemLoteGeneracionCommand(item.ItemId));
                var progreso = await Mediator.Send(new ObtenerLoteGeneracionDocumentosQuery(resultado.Valor.LoteId));
                if (progreso.EsExitoso)
                {
                    var actualizado = progreso.Valor.Items.FirstOrDefault(i => i.ItemId == item.ItemId);
                    if (actualizado is not null)
                    {
                        item.Estado = actualizado.Estado;
                        item.Error = actualizado.Error;
                    }
                }
                else if (resultadoItem.EsFallido)
                {
                    item.Estado = EstadoItemGeneracion.Fallido;
                    item.Error = resultadoItem.Error.Mensaje;
                }

                StateHasChanged();
            }

            Toasts.Mostrar(
                $"Lote terminado: {TotalCompletadosLote} generado(s), {TotalConAvisosLote} con avisos, {TotalFallidosLote} con error.",
                TotalFallidosLote == 0 && TotalConAvisosLote == 0 ? TonoToast.Exito : TonoToast.Advertencia);
        }
        finally
        {
            _procesandoLote = false;
        }
    }

    /// <summary>
    /// REC-186: contenidoPdf llega de <c>ManejarArchivoSeleccionadoAsync</c>
    /// (subida directa por InputFile, hasta <see
    /// cref="TamanoMaximoArchivoBytes"/> = 10 MB — un tope de bytes que un
    /// árbol de páginas compacto no toca, mismo vector que
    /// ConversorArchivosPdf/REC-176) o del blob ya guardado con esos mismos
    /// bytes (<c>CargarVersionExistenteAsync</c>). Es el único de los ocho
    /// sitios de REC-186 con un <c>PdfReader.Open</c> directamente en Web en
    /// vez de en Infrastructure. Pública solo para poder testearla
    /// directamente (función pura, sin InternalsVisibleTo en
    /// CaeManager.Web — mismo patrón que
    /// <c>WebhookWhatsAppEndpoints.FirmaValida</c>).
    /// </summary>
    public static void ComprobarRecuentoDePaginas(byte[] contenidoPdf)
    {
        // ANTES de abrir con PdfReader (más abajo) — mismo patrón que
        // ConversorArchivosPdf (REC-176): abstención (null) no cambia nada,
        // PdfReader.Open sigue siendo la red de seguridad para lo que este
        // pre-escaneo no cubre.
        if (LectorRecuentoPaginasPdfSinAbrir.IntentarLeerRecuentoDePaginasSinAbrir(contenidoPdf) is { } paginasDeclaradas &&
            paginasDeclaradas > MaximoPaginasPlantilla)
        {
            throw new InvalidDataException(
                $"El PDF de la plantilla declara más de {MaximoPaginasPlantilla} páginas y no se puede procesar.");
        }
    }

    /// <summary>Mismo umbral reutilizado que los sitios de Infrastructure — ver <see cref="ComprobarRecuentoDePaginas"/>.</summary>
    private const int MaximoPaginasPlantilla = 2000;

    private async Task<List<PaginaEditor>> RasterizarPaginasAsync(byte[] contenidoPdf)
    {
        ComprobarRecuentoDePaginas(contenidoPdf);

        using var documento = PdfReader.Open(new MemoryStream(contenidoPdf), PdfDocumentOpenMode.Import);

        var paginas = new List<PaginaEditor>();
        for (var i = 0; i < documento.PageCount; i++)
        {
            // El editor sí necesita todas las páginas a la vez —es su razón de
            // ser— pero se piden de una en una: así el pico de memoria es el
            // PNG actual más lo ya convertido a base64, no el doble por tener
            // además la lista completa de byte[] en vuelo.
            var resultado = Rasterizador.RasterizarPagina(contenidoPdf, i);
            if (resultado.EsFallido)
                return [];

            var png = resultado.Valor;
            var paginaPdf = documento.Pages[i];

            paginas.Add(new PaginaEditor
            {
                NumeroPagina = i + 1,
                ImagenBase64 = Convert.ToBase64String(png),
                // El ancho/alto se toma del PDF real, no de la imagen rasterizada: el PNG
                // generado por el rasterizador no trae metadatos de resolución fiables (XImage
                // cae a 96 dpi por defecto aunque se renderizó a 150), lo que inflaba el espacio
                // de coordenadas del editor un ~1,56x respecto al PDF y desplazaba el estampado.
                AnchoPuntos = paginaPdf.Width.Point,
                AltoPuntos = paginaPdf.Height.Point,
            });
        }

        return paginas;
    }

    private async Task EjecutarDeteccionInicialAsync(int carga, FormatoOrigenPlantilla formatoOrigen, byte[] contenidoPdf)
    {
        var deteccion = await Mediator.Send(new DetectarCamposPlantillaQuery(contenidoPdf, formatoOrigen), _ciclo.Token);
        if (!EsVigente(carga) || deteccion.EsFallido || deteccion.Valor.Count == 0) return;

        _elementos = deteccion.Valor.Select(c => new ElementoEditor
        {
            IdLocal = _siguienteIdLocal++,
            Tipo = c.Tipo,
            Pagina = c.Pagina,
            X = c.X,
            Y = c.Y,
            Ancho = c.Ancho,
            Alto = c.Alto,
            EtiquetaVisible = c.EtiquetaVisible,
            FuenteDato = c.FuenteDatoSugerida,
            NombreCampoAcroForm = c.NombreCampoAcroForm,
        }).ToList();

        // «La IA propone» se persiste en silencio: si falla, las cajas siguen
        // en pantalla y el siguiente «Guardar cambios» lo dirá.
        await GuardarElementosAsync(avisarExito: false, avisarFallo: false);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_moduloIniciado || _paginas.Count == 0) return;

        _moduloIniciado = true;
        _referencia = DotNetObjectReference.Create(this);
        _modulo = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/editorPlantilla.js");
        await _modulo.InvokeVoidAsync("iniciar", _referencia, _idContenedor);
    }

    [JSInvokable]
    public void ActualizarPosicionAsync(int idLocal, double x, double y, double ancho, double alto)
    {
        if (_desechado) return;

        var elemento = _elementos.FirstOrDefault(e => e.IdLocal == idLocal);
        if (elemento is null) return;

        elemento.X = x;
        elemento.Y = y;
        elemento.Ancho = ancho;
        elemento.Alto = alto;
        StateHasChanged();
    }

    private void SeleccionarElemento(int idLocal) => _idLocalSeleccionado = idLocal;

    /// <summary>La caja es role="button": Intro y Espacio la seleccionan, como haría un botón.</summary>
    private void SeleccionarConTeclado(KeyboardEventArgs e, int idLocal)
    {
        if (PuedeEditar && e.Key is "Enter" or " ")
            SeleccionarElemento(idLocal);
    }

    private void AnadirElemento()
    {
        var primeraPagina = _paginas.FirstOrDefault()?.NumeroPagina ?? 1;
        var nuevo = new ElementoEditor
        {
            IdLocal = _siguienteIdLocal++,
            Tipo = TipoElementoPlantilla.Texto,
            Pagina = primeraPagina,
            X = 20,
            Y = 20,
            Ancho = AnchoPorDefectoCampo,
            Alto = AltoPorDefectoCampo,
            EtiquetaVisible = "Nuevo campo",
        };
        _elementos.Add(nuevo);
        _idLocalSeleccionado = nuevo.IdLocal;
    }

    private void EliminarElemento(int idLocal)
    {
        _elementos.RemoveAll(e => e.IdLocal == idLocal);
        if (_idLocalSeleccionado == idLocal)
            _idLocalSeleccionado = null;
    }

    /// <summary>
    /// «Eliminar campo» ya no quita la caja al primer clic: pide confirmación
    /// (Editor Plantilla TALVEG.dc.html). La baja sigue siendo local hasta
    /// «Guardar cambios».
    /// </summary>
    private void PedirEliminarElemento(int idLocal) => _idLocalPendienteEliminar = idLocal;

    private void CerrarDialogoEliminar(bool visible)
    {
        if (!visible) _idLocalPendienteEliminar = null;
    }

    private void ConfirmarEliminarElemento()
    {
        if (_idLocalPendienteEliminar is { } idLocal)
            EliminarElemento(idLocal);

        _idLocalPendienteEliminar = null;
    }

    private bool PuedeEditar => _estadoConfiguracion != EstadoConfiguracionPlantilla.Confirmada;

    public const string MensajeFalloGuardado =
        "No pudimos guardar los cambios. Lo que has editado sigue en pantalla: vuelve a intentarlo.";

    public const string MensajeFalloConfirmacion = "No pudimos confirmar la plantilla. Vuelve a intentarlo.";

    /// <summary>
    /// «Guardar cambios». No se guarda mientras se confirma: la confirmación
    /// ya guarda antes, y un segundo guardado en paralelo competiría con ella.
    /// </summary>
    private async Task GuardarDesdeBotonAsync()
    {
        if (_confirmando) return;

        await GuardarElementosAsync(avisarExito: true, avisarFallo: true);
    }

    /// <summary>
    /// Envía la lista completa de cajas (el comando sustituye todos los
    /// elementos de la versión). Tres desenlaces, y ninguno toca lo tecleado:
    /// éxito; rechazo del servidor (versión inexistente o ya confirmada), con
    /// su mensaje; y excepción, con <see cref="MensajeFalloGuardado"/>. No hay
    /// conflicto de concurrencia que distinguir: el comando no lleva versión
    /// de fila. Devuelve si se guardó, para que confirmar no siga adelante
    /// sobre un guardado fallido.
    /// </summary>
    private async Task<bool> GuardarElementosAsync(bool avisarExito, bool avisarFallo)
    {
        // La bandera se levanta antes del primer await: un doble clic encuentra
        // el guardado en curso y no envía un segundo comando.
        if (_desechado || _versionIdActual is not { } versionId || _guardando) return false;

        _guardando = true;
        if (avisarExito) StateHasChanged();

        try
        {
            var dtos = _elementos.Select(e => new ElementoPlantillaEntradaDto(
                e.Tipo, e.Pagina, e.X, e.Y, e.Ancho, e.Alto, e.EtiquetaVisible, e.FuenteDato,
                e.ValorConstante, e.Formato, e.Obligatorio, e.RolFirmante, e.NombreCampoAcroForm)).ToList();

            var resultado = await Mediator.Send(new GuardarElementosPlantillaCommand(versionId, dtos), _ciclo.Token);
            if (_desechado) return false;

            if (resultado.EsFallido)
            {
                if (avisarFallo) Toasts.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return false;
            }

            _estadoConfiguracion = EstadoConfiguracionPlantilla.PendienteRevision;
            if (avisarExito) Toasts.Mostrar("Cambios guardados.", TonoToast.Exito);
            return true;
        }
        catch (Exception) when (_desechado)
        {
            return false;
        }
        catch (Exception)
        {
            // Antes la excepción subía sin capturar y tumbaba el circuito, con
            // lo tecleado dentro.
            if (avisarFallo) Toasts.Mostrar(MensajeFalloGuardado, TonoToast.Error);
            return false;
        }
        finally
        {
            _guardando = false;
        }
    }

    private void PedirConfirmar()
    {
        if (_confirmando || _guardando || _elementos.Count == 0) return;

        _dialogoConfirmarVisible = true;
    }

    private void CerrarDialogoConfirmar(bool visible)
    {
        if (!visible && !_confirmando) _dialogoConfirmarVisible = false;
    }

    /// <summary>
    /// «Sí, confirmar» del diálogo: guarda primero y solo confirma si el
    /// guardado salió bien. Antes confirmaba igualmente tras un guardado
    /// fallido, y la versión quedaba inmutable con los elementos anteriores:
    /// lo tecleado se perdía sin aviso.
    /// </summary>
    private async Task ConfirmarAsync()
    {
        if (_desechado || _versionIdActual is not { } versionId || _confirmando || _guardando) return;

        _confirmando = true;
        StateHasChanged();

        try
        {
            if (!await GuardarElementosAsync(avisarExito: false, avisarFallo: true))
                return;

            var resultado = await Mediator.Send(new ConfirmarPlantillaDocumentoVersionCommand(versionId), _ciclo.Token);
            if (_desechado) return;

            if (resultado.EsFallido)
            {
                Toasts.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            Toasts.Mostrar("Plantilla confirmada.", TonoToast.Exito);
            Navigation.NavigateTo("/plantillas");
        }
        catch (Exception) when (_desechado)
        {
        }
        catch (Exception)
        {
            Toasts.Mostrar(MensajeFalloConfirmacion, TonoToast.Error);
        }
        finally
        {
            _confirmando = false;
            _dialogoConfirmarVisible = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_desechado)
            return;

        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();

        if (_modulo is not null)
        {
            try
            {
                await _modulo.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }

        _referencia?.Dispose();
    }
}
