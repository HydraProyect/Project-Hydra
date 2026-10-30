using System.Reflection;
using System.Text;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CaeManager.Application.Documentos.Queries.DetectarCamposDocumento;
using CaeManager.Web.Features.Documentos.Recursos;
using Microsoft.Extensions.Localization;
using PdfSharp.Pdf;

namespace CaeManager.Web.Tests;

/// <summary>
/// Hallazgo de las pruebas de flujos del 21-10: el PDF elegido no se pudo procesar, salía
/// el toast de error y, al pulsar Guardar, también «Documento creado correctamente». El
/// Documento quedaba sin archivo y contaba como al día. Ahora un archivo que no llega a
/// adjuntarse bloquea el guardado hasta adjuntar otro: solo se ve el error.
/// </summary>
public class DrawerGestionDocumentoArchivoNoAdjuntadoTests : BunitContext
{
    private sealed class MediatorQueAnotaComandos : IMediator
    {
        public List<CrearDocumentoCommand> Creados { get; } = [];
        public List<byte[]> Detectados { get; } = [];
        public List<long> ReservadoAlDetectar { get; } = [];
        public Func<long> LeerReservado { get; set; } = () => 0;

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object respuesta = request switch
            {
                ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)[],
                ObtenerTiposDocumentoQuery => (IReadOnlyList<TipoDocumentoListaDto>)[],
                CrearDocumentoCommand crear => Anotar(crear),
                DetectarCamposDocumentoQuery detectar => AnotarDeteccion(detectar),
                // La detección automática de campos es de mejor esfuerzo: si falla, el drawer sigue.
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)respuesta);
        }

        private Result<DeteccionCamposDocumentoDto> AnotarDeteccion(DetectarCamposDocumentoQuery consulta)
        {
            Detectados.Add(consulta.Contenido);
            ReservadoAlDetectar.Add(LeerReservado());
            return Result.Exito(new DeteccionCamposDocumentoDto(null, null, 0));
        }

        private Result<Guid> Anotar(CrearDocumentoCommand comando)
        {
            Creados.Add(comando);
            return Result.Exito(Guid.NewGuid());
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    /// <summary>Devuelve las claves en orden; una clave null simula que el almacén falla.</summary>
    private sealed class AlmacenGuionizado : IFileStorageService
    {
        public Queue<string?> Respuestas { get; } = new();
        public List<string> Borrados { get; } = [];
        public byte[]? ContenidoGuardado { get; private set; }

        /// <summary>Lo que hace el almacén antes de cargar el contenido: el real reserva aquí su propia memoria.</summary>
        public Func<CancellationToken, Task>? AlGuardar { get; set; }

        public async Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
        {
            if (AlGuardar is { } alGuardar)
                await alGuardar(cancellationToken);
            using var copia = new MemoryStream();
            await contenido.CopyToAsync(copia, cancellationToken);
            ContenidoGuardado = copia.ToArray();
            return Respuestas.Dequeue() ?? throw new IOException("El almacén no responde.");
        }

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Este test no abre nada.");

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default)
        {
            Borrados.Add(identificador);
            return Task.CompletedTask;
        }
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Este test no convierte nada.");
    }

    private readonly MediatorQueAnotaComandos _mediator = new();
    private readonly AlmacenGuionizado _almacen = new();
    private readonly PresupuestoMemoriaDocumentos _presupuesto = new(
        Options.Create(new OpcionesPresupuestoMemoriaDocumentos { EsperaMaxima = TimeSpan.FromMilliseconds(20) }), TimeProvider.System);

    public DrawerGestionDocumentoArchivoNoAdjuntadoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddSingleton<IPresupuestoMemoriaDocumentos>(_presupuesto);
        _mediator.LeerReservado = () => _presupuesto.Reservado;
    }

    private async Task<IRenderedComponent<DrawerGestionDocumento>> AbrirAltaListaParaGuardarAsync()
    {
        Services.AddScoped<IMediator>(_ => _mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<IFileStorageService>(_ => _almacen);
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var cut = Render<DrawerGestionDocumento>();
        await cut.InvokeAsync(() => cut.Instance.AbrirCrearAsync());
        // Trabajador y tipo elegidos: sin el bloqueo, Guardar llegaría al comando.
        await cut.InvokeAsync(() =>
        {
            Escribir(cut.Instance, "_trabajadorId", Guid.NewGuid().ToString());
            Escribir(cut.Instance, "_tipoDocumentoId", Guid.NewGuid().ToString());
        });
        cut.Render();
        return cut;
    }

    private static void Escribir(DrawerGestionDocumento instancia, string nombre, object valor)
    {
        var campo = typeof(DrawerGestionDocumento).GetField(nombre, BindingFlags.Instance | BindingFlags.NonPublic);
        campo.Should().NotBeNull($"el test necesita escribir {nombre} directamente");
        campo!.SetValue(instancia, valor);
    }

    private static byte[] CrearPdf()
    {
        using var documento = new PdfDocument();
        documento.AddPage();
        using var salida = new MemoryStream();
        documento.Save(salida);
        return salida.ToArray();
    }

    /// <summary>Empieza como un PDF (pasa la comprobación de firma) pero no tiene tabla xref: PdfSharp no lo abre.</summary>
    private static byte[] CrearPdfDanado() => Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nbasura sin xref\n");

    private static void Subir(IRenderedComponent<DrawerGestionDocumento> cut, byte[] contenido) =>
        cut.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromBinary(contenido, "documento-identidad.pdf", contentType: "application/pdf"));

    private static Task PulsarGuardarAsync(IRenderedComponent<DrawerGestionDocumento> cut) =>
        cut.Find(".drawer-pie .boton-primario").ClickAsync(new MouseEventArgs());

    private IReadOnlyList<ToastMensaje> Toasts => Services.GetRequiredService<ToastService>().Mensajes;

    [Fact]
    public async Task Reserva_liberada_si_falla_el_procesamiento()
    {
        var cut = await AbrirAltaListaParaGuardarAsync();
        Subir(cut, CrearPdfDanado());
        _presupuesto.Reservado.Should().Be(0);
        Toasts.Should().Contain(t => t.Tono == TonoToast.Error && t.Mensaje == "No pudimos procesar el archivo. Intenta nuevamente.");
    }

    [Fact]
    public async Task Sin_hueco_muestra_el_mismo_error_y_no_adjunta()
    {
        var cut = await AbrirAltaListaParaGuardarAsync();
        using var ocupado = await _presupuesto.ReservarAsync(_presupuesto.Maximo);
        Subir(cut, CrearPdf());
        var mensaje = Services.GetRequiredService<IStringLocalizer<TextosSubidaMasiva>>()["ErrorSinEspacio"].Value;
        cut.WaitForAssertion(() => Toasts.Should().Contain(t => t.Tono == TonoToast.Error && t.Mensaje == mensaje));
        _almacen.ContenidoGuardado.Should().BeNull("sin hueco no se llega a cargar ni a guardar nada");
        _mediator.Detectados.Should().BeEmpty();
        await PulsarGuardarAsync(cut);
        _mediator.Creados.Should().BeEmpty();
        _presupuesto.Reservado.Should().Be(_presupuesto.Maximo);
    }

    /// <summary>
    /// Varias páginas de un mismo documento: la lectura reserva cuatro veces la suma de
    /// lo declarado, ni un byte menos. Con exactamente ese hueco entra; con uno menos, no.
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task Con_varias_paginas_la_lectura_reserva_cuatro_veces_la_suma(long bytesDeMenos, bool entra)
    {
        var cut = await AbrirAltaListaParaGuardarAsync();
        _almacen.Respuestas.Enqueue("blob-bueno");
        var primera = CrearPdf();
        var segunda = CrearPdf();
        var necesario = 4L * (primera.Length + segunda.Length);
        using var ocupado = await _presupuesto.ReservarAsync(_presupuesto.Maximo - necesario + bytesDeMenos);
        var mensaje = Services.GetRequiredService<IStringLocalizer<TextosSubidaMasiva>>()["ErrorSinEspacio"].Value;

        cut.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromBinary(primera, "pagina-1.pdf", contentType: "application/pdf"),
            InputFileContent.CreateFromBinary(segunda, "pagina-2.pdf", contentType: "application/pdf"));

        if (entra)
        {
            cut.WaitForAssertion(() => _almacen.ContenidoGuardado.Should().NotBeNull());
            Toasts.Should().NotContain(t => t.Mensaje == mensaje);
        }
        else
        {
            cut.WaitForAssertion(() => Toasts.Should().Contain(t => t.Tono == TonoToast.Error && t.Mensaje == mensaje));
            _almacen.ContenidoGuardado.Should().BeNull();
        }
    }

    /// <summary>Sin hueco al guardar (el almacén no pudo reservar): mismo aviso, no queda adjunto y no retiene nada.</summary>
    [Fact]
    public async Task Sin_hueco_al_guardar_muestra_el_mismo_error_y_no_adjunta()
    {
        var cut = await AbrirAltaListaParaGuardarAsync();
        _almacen.AlGuardar = _ => throw new PresupuestoMemoriaAgotadoException();
        var mensaje = Services.GetRequiredService<IStringLocalizer<TextosSubidaMasiva>>()["ErrorSinEspacio"].Value;

        Subir(cut, CrearPdf());

        cut.WaitForAssertion(() => Toasts.Should().Contain(t => t.Tono == TonoToast.Error && t.Mensaje == mensaje));
        Toasts.Should().NotContain(t => t.Mensaje == "No pudimos procesar el archivo. Intenta nuevamente.");
        _presupuesto.Reservado.Should().Be(0);
        _mediator.Detectados.Should().BeEmpty();
        cut.WaitForAssertion(() => cut.FindAll(".campo-mensaje-error").Should().ContainSingle()
            .Which.TextContent.Should().Be("Este archivo no se adjuntó. Elige otro o vuelve a intentarlo."));
        await PulsarGuardarAsync(cut);
        _mediator.Creados.Should().BeEmpty();
    }

    /// <summary>
    /// Doble reserva: el almacenamiento real reserva su propia memoria al guardar
    /// (DiskFileStorageService.GuardarAsync). Si el formulario retuviera la suya —con
    /// varias páginas puede ocupar casi todo el presupuesto—, el guardado esperaría a la
    /// reserva de su mismo formulario hasta rechazarse aunque no hubiera ninguna otra
    /// subida. El caso extremo: el guardado pide el presupuesto entero.
    /// </summary>
    [Fact]
    public async Task El_guardado_no_espera_a_la_reserva_del_propio_formulario()
    {
        var cut = await AbrirAltaListaParaGuardarAsync();
        _almacen.Respuestas.Enqueue("blob-bueno");
        long? reservadoAlGuardar = null;
        _almacen.AlGuardar = async token =>
        {
            reservadoAlGuardar = _presupuesto.Reservado;
            using var propia = await _presupuesto.ReservarAsync(_presupuesto.Maximo, token);
        };

        Subir(cut, CrearPdf());

        cut.WaitForAssertion(() => _mediator.Detectados.Should().ContainSingle());
        reservadoAlGuardar.Should().Be(0, "la reserva de la lectura se devuelve antes de guardar");
        Toasts.Should().NotContain(t => t.Tono == TonoToast.Error);
        _presupuesto.Reservado.Should().Be(0);
        await PulsarGuardarAsync(cut);
        _mediator.Creados.Should().ContainSingle().Which.ArchivoUrl.Should().Be("blob-bueno");
    }

    /// <summary>La detección IA es de mejor esfuerzo: sin hueco para ella, el archivo ya guardado sigue adjunto.</summary>
    [Fact]
    public async Task Sin_hueco_para_la_deteccion_el_archivo_queda_adjunto_sin_detectar()
    {
        var cut = await AbrirAltaListaParaGuardarAsync();
        _almacen.Respuestas.Enqueue("blob-bueno");
        ReservaMemoriaDocumentos? ocupado = null;
        // Otra subida ocupa el presupuesto justo cuando este formulario termina de guardar.
        _almacen.AlGuardar = async token => ocupado = await _presupuesto.ReservarAsync(_presupuesto.Maximo, token);

        try
        {
            Subir(cut, CrearPdf());

            var subiendo = typeof(DrawerGestionDocumento).GetField("_subiendoArchivo", BindingFlags.Instance | BindingFlags.NonPublic)!;
            // La otra reserva se toma al guardar; después, el formulario termina sin detectar.
            cut.WaitForAssertion(() =>
            {
                ocupado.Should().NotBeNull();
                subiendo.GetValue(cut.Instance).Should().Be(false);
            });
            _mediator.Detectados.Should().BeEmpty();
            Toasts.Should().NotContain(t => t.Tono == TonoToast.Error);
            _presupuesto.Reservado.Should().Be(_presupuesto.Maximo, "solo queda la reserva de la otra subida");
            cut.FindAll(".campo-mensaje-error").Should().BeEmpty();
            await PulsarGuardarAsync(cut);
            _mediator.Creados.Should().ContainSingle().Which.ArchivoUrl.Should().Be("blob-bueno");
        }
        finally
        {
            ocupado?.Dispose();
        }
    }

    [Fact]
    public async Task Un_pdf_que_no_se_puede_procesar_bloquea_guardar_y_no_dice_creado()
    {
        var cut = await AbrirAltaListaParaGuardarAsync();

        Subir(cut, CrearPdfDanado());

        cut.FindAll(".campo-mensaje-error").Should().ContainSingle()
            .Which.TextContent.Should().Be("Este archivo no se adjuntó. Elige otro o vuelve a intentarlo.");
        cut.Markup.Should().NotContain("Archivo adjuntado correctamente.");

        await PulsarGuardarAsync(cut);

        _mediator.Creados.Should().BeEmpty("sin el archivo que el Gestor CAE cree haber subido no se crea el Documento");
        Toasts.Should().NotContain(t => t.Tono == TonoToast.Exito, "la pantalla no puede decir a la vez error y éxito");
        Toasts.Should().Contain(t => t.Tono == TonoToast.Error && t.Mensaje == "No pudimos procesar el archivo. Intenta nuevamente.");
        cut.Markup.Should().Contain("No se ha guardado: el archivo que elegiste no se adjuntó. Vuelve a adjuntarlo antes de guardar.");
        cut.FindAll(".drawer-panel").Should().NotBeEmpty("el formulario sigue abierto para adjuntar otro archivo");
    }

    [Fact]
    public async Task Si_falla_el_almacen_tampoco_se_guarda()
    {
        var cut = await AbrirAltaListaParaGuardarAsync();
        _almacen.Respuestas.Enqueue(null);

        Subir(cut, CrearPdf());
        await PulsarGuardarAsync(cut);

        _mediator.Creados.Should().BeEmpty();
        Toasts.Should().NotContain(t => t.Tono == TonoToast.Exito);
    }

    /// <summary>
    /// Elegir otro archivo descarta el anterior antes de guardar el nuevo; si el nuevo falla,
    /// el formulario no puede seguir apuntando a la clave ya borrada.
    /// </summary>
    [Fact]
    public async Task Un_segundo_archivo_que_falla_no_deja_el_formulario_apuntando_al_primero_ya_borrado()
    {
        var cut = await AbrirAltaListaParaGuardarAsync();
        _almacen.Respuestas.Enqueue("blob-primero");
        _almacen.Respuestas.Enqueue(null);

        Subir(cut, CrearPdf());
        Subir(cut, CrearPdf());

        _almacen.Borrados.Should().Equal("blob-primero");
        typeof(DrawerGestionDocumento).GetField("_archivoUrl", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cut.Instance).Should().BeNull("la clave del primero ya no existe en el almacén");
        await PulsarGuardarAsync(cut);
        _mediator.Creados.Should().BeEmpty();
    }

    [Fact]
    public async Task Adjuntar_otro_archivo_que_si_entra_desbloquea_el_guardado()
    {
        var cut = await AbrirAltaListaParaGuardarAsync();
        _almacen.Respuestas.Enqueue("blob-bueno");

        Subir(cut, CrearPdfDanado());
        await PulsarGuardarAsync(cut);
        cut.Markup.Should().Contain("No se ha guardado:");

        Subir(cut, CrearPdf());

        cut.FindAll(".campo-mensaje-error").Should().BeEmpty();
        _mediator.Detectados.Should().ContainSingle().Which.Should().Equal(_almacen.ContenidoGuardado!);
        _mediator.ReservadoAlDetectar.Should().ContainSingle()
            .Which.Should().Be((long)_almacen.ContenidoGuardado!.Length * 4, "la detección IA reserva el PDF y lo que deriva de él");
        _presupuesto.Reservado.Should().Be(0);
        cut.Markup.Should().NotContain("No se ha guardado:", "con el archivo ya adjunto el aviso del bloqueo deja de aplicar");
        await PulsarGuardarAsync(cut);

        _mediator.Creados.Should().ContainSingle().Which.ArchivoUrl.Should().Be("blob-bueno");
        Toasts.Should().Contain(t => t.Tono == TonoToast.Exito && t.Mensaje == "Documento creado correctamente.");
    }

    [Fact]
    public async Task Reabrir_el_formulario_empieza_sin_el_bloqueo()
    {
        var cut = await AbrirAltaListaParaGuardarAsync();
        Subir(cut, CrearPdfDanado());

        await cut.InvokeAsync(() => cut.Instance.AbrirCrearAsync());

        cut.FindAll(".campo-mensaje-error").Should().BeEmpty("cada apertura empieza sin archivo elegido");
    }

    /// <summary>
    /// Salida explícita del bloqueo: seguir sin archivo es una decisión visible, no el guardado
    /// silencioso de antes. Un archivo anterior que sí subió, y que ya no se va a adoptar, se borra.
    /// </summary>
    [Fact]
    public async Task Seguir_sin_archivo_tras_un_rechazo_descarta_lo_subido_y_guarda_sin_archivo()
    {
        var cut = await AbrirAltaListaParaGuardarAsync();
        _almacen.Respuestas.Enqueue("blob-anterior");

        Subir(cut, CrearPdf());
        cut.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromBinary([1, 2, 3], "notas.txt", contentType: "text/plain"));
        await cut.Find(".boton-descartar-archivo-no-adjuntado").ClickAsync(new MouseEventArgs());

        _almacen.Borrados.Should().Equal("blob-anterior");
        cut.FindAll(".campo-mensaje-error").Should().BeEmpty();
        await PulsarGuardarAsync(cut);

        _mediator.Creados.Should().ContainSingle().Which.ArchivoUrl.Should().BeNull();
    }
}
