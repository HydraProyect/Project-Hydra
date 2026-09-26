using System.Reflection;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2: <see cref="DrawerGestionDocumento"/> vive dentro de otras pantallas (Documentos,
/// Trabajador 360, los acordeones de Centro y Subcontrata…). Salir de cualquiera de ellas con
/// el formulario a medias pregunta antes; confirmar la salida borra además el archivo ya
/// subido que ningún comando adoptó, igual que cerrar el drawer.
/// </summary>
public class DrawerGestionDocumentoAvisoCambiosSinGuardarTests : BunitContext
{
    private sealed class MediatorFalso : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)[],
                ObtenerTiposDocumentoQuery => (IReadOnlyList<TipoDocumentoListaDto>)[],
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }));

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

    private sealed class AlmacenQueAnotaBorrados : IFileStorageService
    {
        public List<string> Borrados { get; } = [];

        /// <summary>El almacenamiento no responde hasta que el test lo suelta: así se puede cerrar el formulario a mitad de la subida.</summary>
        public TaskCompletionSource<string> GuardadoPendiente { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            GuardadoPendiente.Task;

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

    private readonly AlmacenQueAnotaBorrados _almacen = new();

    public DrawerGestionDocumentoAvisoCambiosSinGuardarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private IRenderedComponent<DrawerGestionDocumento> Renderizar()
    {
        Services.AddScoped<IMediator>(_ => new MediatorFalso());
        Services.AddScoped<ToastService>();
        Services.AddScoped<IFileStorageService>(_ => _almacen);
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        return Render<DrawerGestionDocumento>();
    }

    /// <summary>
    /// Simula el resultado de la subida (el archivo ya está en el almacén y en el formulario,
    /// sin adoptar): subir de verdad exige un InputFile y el conversor, que no son lo que se mide.
    /// </summary>
    private static void SimularArchivoSubidoSinAdoptar(DrawerGestionDocumento instancia, string identificador)
    {
        foreach (var nombre in new[] { "_archivoUrl", "_archivoUrlSubidoSinAdoptar" })
        {
            var campo = typeof(DrawerGestionDocumento).GetField(nombre, BindingFlags.Instance | BindingFlags.NonPublic);
            campo.Should().NotBeNull($"el test necesita escribir {nombre} directamente");
            campo!.SetValue(instancia, identificador);
        }
    }

    [Fact]
    public async Task Salir_con_comentarios_escritos_pregunta_y_deja_seguir_editando()
    {
        var cut = Renderizar();
        var navegacion = Services.GetRequiredService<NavigationManager>();
        var origen = navegacion.Uri;
        await cut.InvokeAsync(() => cut.Instance.AbrirCrearAsync());
        await cut.Find(".drawer-panel textarea").InputAsync(new ChangeEventArgs { Value = "Firmado por el jefe de obra" });

        await cut.InvokeAsync(() => navegacion.NavigateTo("/trabajadores"));

        navegacion.Uri.Should().Be(origen, "con el formulario a medias la navegación se detiene");
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Seguir editando").ClickAsync(new MouseEventArgs());

        cut.FindAll(".modal-contenido").Should().BeEmpty();
        cut.FindComponents<CampoTextarea>().Should().Contain(c => c.Instance.Valor == "Firmado por el jefe de obra");
    }

    [Fact]
    public async Task Salir_y_descartar_borra_el_archivo_subido_que_nadie_adopto()
    {
        var cut = Renderizar();
        var navegacion = Services.GetRequiredService<NavigationManager>();
        await cut.InvokeAsync(() => cut.Instance.AbrirCrearAsync());
        await cut.InvokeAsync(() => SimularArchivoSubidoSinAdoptar(cut.Instance, "blob-sin-adoptar"));
        // La subida real termina en un render; escribir los campos por reflexión, no.
        cut.Render();

        await cut.InvokeAsync(() => navegacion.NavigateTo("/trabajadores"));
        _almacen.Borrados.Should().BeEmpty("preguntar no descarta nada todavía");
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Salir y descartar").ClickAsync(new MouseEventArgs());

        navegacion.Uri.Should().EndWith("/trabajadores");
        _almacen.Borrados.Should().Equal("blob-sin-adoptar");
        cut.FindAll(".drawer-panel").Should().BeEmpty(
            "si la navegación no desmonta la página que lo aloja, el drawer no puede quedar abierto apuntando al archivo borrado");
    }

    /// <summary>
    /// Revisión Codex: con un guardado en curso el comando puede estar adoptando el archivo;
    /// confirmar la salida en ese momento no lo borra (lo contrario dejaría un Documento
    /// apuntando a un archivo que ya no existe).
    /// </summary>
    [Fact]
    public async Task Salir_y_descartar_con_un_guardado_en_curso_no_borra_el_archivo()
    {
        var cut = Renderizar();
        var navegacion = Services.GetRequiredService<NavigationManager>();
        await cut.InvokeAsync(() => cut.Instance.AbrirCrearAsync());
        await cut.InvokeAsync(() =>
        {
            SimularArchivoSubidoSinAdoptar(cut.Instance, "blob-en-guardado");
            var guardando = typeof(DrawerGestionDocumento).GetField("_guardando", BindingFlags.Instance | BindingFlags.NonPublic);
            guardando.Should().NotBeNull("el test necesita simular el guardado en curso");
            guardando!.SetValue(cut.Instance, true);
        });
        cut.Render();

        await cut.InvokeAsync(() => navegacion.NavigateTo("/trabajadores"));
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Salir y descartar").ClickAsync(new MouseEventArgs());

        navegacion.Uri.Should().EndWith("/trabajadores");
        _almacen.Borrados.Should().BeEmpty("el comando en curso puede estar adoptando ese archivo");
    }

    [Fact]
    public async Task Abrir_sin_tocar_nada_y_salir_no_pregunta()
    {
        var cut = Renderizar();
        var navegacion = Services.GetRequiredService<NavigationManager>();
        await cut.InvokeAsync(() => cut.Instance.AbrirCrearParaFaltanteAsync(Guid.NewGuid(), Guid.NewGuid()));

        await cut.InvokeAsync(() => navegacion.NavigateTo("/trabajadores"));

        navegacion.Uri.Should().EndWith("/trabajadores", "lo preseleccionado por la pantalla que abre no es un cambio de quien mira");
        cut.FindAll(".modal-contenido").Should().BeEmpty();
    }

    /// <summary>P1-E2b: cerrar el drawer con la X con comentarios escritos pregunta «¿Descartar cambios?».</summary>
    [Fact]
    public async Task Cerrar_con_la_X_con_comentarios_escritos_pregunta()
    {
        var cut = Renderizar();
        await cut.InvokeAsync(() => cut.Instance.AbrirCrearAsync());
        await cut.Find(".drawer-panel textarea").InputAsync(new ChangeEventArgs { Value = "Firmado por el jefe de obra" });

        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());

        cut.FindAll(".drawer-panel").Should().NotBeEmpty("con comentarios escritos la X pregunta antes de cerrar");
        cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "¿Descartar cambios?");
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Descartar cambios").ClickAsync(new MouseEventArgs());
        cut.FindAll(".drawer-panel").Should().BeEmpty();
    }

    private static byte[] CrearPdf()
    {
        using var documento = new PdfDocument();
        documento.AddPage();
        using var salida = new MemoryStream();
        documento.Save(salida);
        return salida.ToArray();
    }

    /// <summary>
    /// Revisión Codex (ronda 2): mientras el archivo se lee, convierte y almacena,
    /// _archivoUrl aún no ha cambiado; la subida en curso cuenta por sí misma como cambio.
    /// Y si se confirma la salida en ese momento, el archivo que el almacén devuelva después
    /// ya no lo va a adoptar nadie: se borra en cuanto llega.
    /// </summary>
    [Fact]
    public async Task Salir_durante_una_subida_pregunta_y_el_archivo_que_llega_tarde_se_borra()
    {
        var cut = Renderizar();
        var navegacion = Services.GetRequiredService<NavigationManager>();
        await cut.InvokeAsync(() => cut.Instance.AbrirCrearAsync());
        var entrada = cut.FindComponent<InputFile>();
        // UploadFiles espera a que termine el manejador, y el manejador espera al almacén que
        // este test retiene: se lanza aparte para poder actuar a mitad de la subida.
        var subida = Task.Run(() => entrada.UploadFiles(InputFileContent.CreateFromBinary(CrearPdf(), "prl.pdf", contentType: "application/pdf")));
        cut.WaitForAssertion(() => cut.FindComponent<ZonaSoltarArchivo>().Instance.Cargando.Should().BeTrue("la subida tiene que estar en curso para que esto mida algo"));

        await cut.InvokeAsync(() => navegacion.NavigateTo("/trabajadores"));

        cut.FindAll(".modal-contenido").Should().ContainSingle("con una subida en curso salir se detiene y pregunta");
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Salir y descartar").ClickAsync(new MouseEventArgs());
        navegacion.Uri.Should().EndWith("/trabajadores");

        _almacen.GuardadoPendiente.SetResult("blob-tardio");
        await subida.WaitAsync(TimeSpan.FromSeconds(10));

        cut.WaitForAssertion(() => _almacen.Borrados.Should().Equal(["blob-tardio"],
            "el formulario ya se cerró: nadie va a adoptar el archivo que el almacén devolvió después"));
    }

    /// <summary>
    /// Revisión Codex (ronda 2): el aviso se pinta después de los demás diálogos del drawer.
    /// Con el mismo z-index, el último del DOM queda encima; si el aviso fuera antes, la
    /// pregunta quedaría detrás del diálogo de vigencia anterior y la salida se bloquearía
    /// sin ofrecer sus botones.
    /// </summary>
    [Fact]
    public async Task El_aviso_queda_encima_de_otro_dialogo_abierto()
    {
        var cut = Renderizar();
        var navegacion = Services.GetRequiredService<NavigationManager>();
        await cut.InvokeAsync(() => cut.Instance.AbrirCrearAsync());
        await cut.Find(".drawer-panel textarea").InputAsync(new ChangeEventArgs { Value = "Pendiente de sello" });
        await cut.InvokeAsync(() =>
        {
            var dialogo = typeof(DrawerGestionDocumento).GetField("_confirmarVigenciaAnteriorVisible", BindingFlags.Instance | BindingFlags.NonPublic);
            dialogo.Should().NotBeNull("el test necesita abrir el diálogo de vigencia anterior");
            dialogo!.SetValue(cut.Instance, true);
        });
        cut.Render();

        await cut.InvokeAsync(() => navegacion.NavigateTo("/trabajadores"));

        var dialogos = cut.FindAll(".modal-contenido");
        dialogos.Should().HaveCount(2, "el diálogo de vigencia y el aviso de salida conviven");
        dialogos.Last().TextContent.Should().Contain("¿Salir sin guardar?", "el aviso tiene que ser el último del DOM para quedar encima");
    }
}
