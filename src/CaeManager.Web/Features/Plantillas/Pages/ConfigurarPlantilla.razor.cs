using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Plantillas.Commands.ConfirmarPlantillaDocumentoVersion;
using CaeManager.Application.Plantillas.Commands.CrearPlantillaDocumento;
using CaeManager.Application.Plantillas.Commands.GuardarElementosPlantilla;
using CaeManager.Application.Plantillas.Queries.DetectarCamposPlantilla;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillaDocumentoVersion;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using PdfSharp.Drawing;
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
        if (PlantillaDocumentoVersionId == _versionIdActual) return;

        if (PlantillaDocumentoVersionId is not { } versionId)
        {
            _versionIdActual = null;
            return;
        }

        await CargarVersionExistenteAsync(versionId);
    }

    private async Task CargarVersionExistenteAsync(Guid versionId)
    {
        _cargandoEditor = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ObtenerPlantillaDocumentoVersionQuery(versionId));
            if (resultado.EsFallido)
            {
                _errorCarga = true;
                return;
            }

            var detalle = resultado.Valor;

            await using var flujo = await AlmacenamientoArchivos.AbrirAsync(detalle.ArchivoOriginalUrl);
            using var memoria = new MemoryStream();
            await flujo.CopyToAsync(memoria);
            var contenido = memoria.ToArray();

            await IniciarEditorAsync(
                versionId, detalle.PlantillaDocumentoId, detalle.NombrePlantilla, detalle.FormatoOrigen,
                detalle.EstadoConfiguracion, contenido, ElementosIniciales(detalle));
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            _cargandoEditor = false;
            StateHasChanged();
        }
    }

    private List<ElementoEditor> ElementosIniciales(PlantillaDocumentoVersionDetalleDto detalle) =>
        detalle.Elementos.Select(e => new ElementoEditor
        {
            IdLocal = _siguienteIdLocal++,
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

            await IniciarEditorAsync(
                resultado.Valor.PlantillaDocumentoVersionId, resultado.Valor.PlantillaDocumentoId, _nombre,
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
        Guid versionId, Guid documentoId, string nombrePlantilla, FormatoOrigenPlantilla formatoOrigen,
        EstadoConfiguracionPlantilla estadoConfiguracion, byte[] contenidoPdf, List<ElementoEditor> elementosExistentes)
    {
        _versionIdActual = versionId;
        _documentoId = documentoId;
        _nombrePlantilla = nombrePlantilla;
        _formatoOrigen = formatoOrigen;
        _estadoConfiguracion = estadoConfiguracion;
        _elementos = elementosExistentes;
        _idLocalSeleccionado = null;

        _paginas = await RasterizarPaginasAsync(contenidoPdf);

        if (_elementos.Count == 0 && estadoConfiguracion == EstadoConfiguracionPlantilla.Borrador)
            await EjecutarDeteccionInicialAsync(versionId, formatoOrigen, contenidoPdf);
    }

    private async Task<List<PaginaEditor>> RasterizarPaginasAsync(byte[] contenidoPdf)
    {
        using var documento = PdfReader.Open(new MemoryStream(contenidoPdf), PdfDocumentOpenMode.Import);
        var indices = Enumerable.Range(0, documento.PageCount).ToList();

        var resultado = Rasterizador.RasterizarPaginas(contenidoPdf, indices);
        if (resultado.EsFallido)
            return [];

        var paginas = new List<PaginaEditor>();
        for (var i = 0; i < resultado.Valor.Count; i++)
        {
            var png = resultado.Valor[i];
            using var xImagen = XImage.FromStream(new MemoryStream(png));
            var anchoPuntos = xImagen.PixelWidth * 72.0 / xImagen.HorizontalResolution;
            var altoPuntos = xImagen.PixelHeight * 72.0 / xImagen.VerticalResolution;

            paginas.Add(new PaginaEditor
            {
                NumeroPagina = i + 1,
                ImagenBase64 = Convert.ToBase64String(png),
                AnchoPuntos = anchoPuntos,
                AltoPuntos = altoPuntos,
            });
        }

        return paginas;
    }

    private async Task EjecutarDeteccionInicialAsync(Guid versionId, FormatoOrigenPlantilla formatoOrigen, byte[] contenidoPdf)
    {
        var deteccion = await Mediator.Send(new DetectarCamposPlantillaQuery(contenidoPdf, formatoOrigen));
        if (deteccion.EsFallido || deteccion.Valor.Count == 0) return;

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

        await GuardarCambiosAsync(mostrarToast: false);
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
        var elemento = _elementos.FirstOrDefault(e => e.IdLocal == idLocal);
        if (elemento is null) return;

        elemento.X = x;
        elemento.Y = y;
        elemento.Ancho = ancho;
        elemento.Alto = alto;
        StateHasChanged();
    }

    private void SeleccionarElemento(int idLocal) => _idLocalSeleccionado = idLocal;

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

    private bool PuedeEditar => _estadoConfiguracion != EstadoConfiguracionPlantilla.Confirmada;

    private async Task GuardarCambiosAsync(bool mostrarToast = true)
    {
        if (_versionIdActual is not { } versionId || _guardando) return;

        _guardando = true;
        if (mostrarToast) StateHasChanged();

        try
        {
            var dtos = _elementos.Select(e => new ElementoPlantillaEntradaDto(
                e.Tipo, e.Pagina, e.X, e.Y, e.Ancho, e.Alto, e.EtiquetaVisible, e.FuenteDato,
                e.ValorConstante, e.Formato, e.Obligatorio, e.RolFirmante, e.NombreCampoAcroForm)).ToList();

            var resultado = await Mediator.Send(new GuardarElementosPlantillaCommand(versionId, dtos));
            if (resultado.EsFallido)
            {
                if (mostrarToast) Toasts.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            _estadoConfiguracion = EstadoConfiguracionPlantilla.PendienteRevision;
            if (mostrarToast) Toasts.Mostrar("Cambios guardados.", TonoToast.Exito);
        }
        finally
        {
            _guardando = false;
        }
    }

    private async Task ConfirmarAsync()
    {
        if (_versionIdActual is not { } versionId || _confirmando) return;

        _confirmando = true;
        StateHasChanged();

        try
        {
            await GuardarCambiosAsync(mostrarToast: false);

            var resultado = await Mediator.Send(new ConfirmarPlantillaDocumentoVersionCommand(versionId));
            if (resultado.EsFallido)
            {
                Toasts.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            Toasts.Mostrar("Plantilla confirmada.", TonoToast.Exito);
            Navigation.NavigateTo("/plantillas");
        }
        finally
        {
            _confirmando = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
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
