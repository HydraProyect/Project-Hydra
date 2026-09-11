using Bunit;
using CaeManager.Application.Documentos.Commands.GuardarFirmaGuardadaUsuario;
using CaeManager.Application.Documentos.Queries.ObtenerFirmaGuardadaUsuario;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Usuarios.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace CaeManager.Web.Tests;

/// <summary>
/// /mi-firma en su versión Gen 2. Lo que se prueba aquí es lo que la pantalla
/// hace de nuevo o de distinto respecto a la versión anterior —estado sin
/// firma, estado de error con «Reintentar», pista del botón Guardar, la URL
/// de la imagen que cambia al guardar, el enganche del lienzo y los tres
/// desenlaces de un guardado—, no el lienzo en sí, que vive en
/// firmaEnCampo.js y no se ejecuta bajo bUnit.
/// </summary>
public class MiFirmaTests : BunitContext
{
    private const string Modulo = "./js/firmaEnCampo.js";
    private const string MensajeFalloGuardado = "No pudimos guardar tu firma";
    private const string MensajeFalloLectura = "No se pudo leer el archivo";

    private readonly BunitJSModuleInterop _modulo;

    public MiFirmaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _modulo = JSInterop.SetupModule(Modulo);
        _modulo.Mode = JSRuntimeMode.Loose;
    }

    private sealed class MediatorFirma : IMediator
    {
        /// <summary>Respuestas sucesivas a la consulta de la firma; la última se repite.</summary>
        public required Queue<Func<FirmaGuardadaUsuarioDto?>> Consultas { get; init; }

        /// <summary>Respuesta al comando de guardar; puede lanzar para simular un fallo no controlado.</summary>
        public Func<Result> Guardar { get; set; } = Result.Exito;

        public int GuardadosRecibidos { get; private set; }

        private Func<FirmaGuardadaUsuarioDto?>? _ultima;

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerFirmaGuardadaUsuarioQuery:
                    _ultima = Consultas.Count > 0 ? Consultas.Dequeue() : _ultima;
                    return Task.FromResult((TResponse)(object)_ultima!()!);
                case GuardarFirmaGuardadaUsuarioCommand:
                    GuardadosRecibidos++;
                    return Task.FromResult((TResponse)(object)Guardar());
                default:
                    throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");
            }
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

    private MediatorFirma _mediator = null!;

    private IRenderedComponent<MiFirma> Renderizar(params Func<FirmaGuardadaUsuarioDto?>[] consultas)
    {
        _mediator = new MediatorFirma { Consultas = new Queue<Func<FirmaGuardadaUsuarioDto?>>(consultas) };
        Services.AddScoped<IMediator>(_ => _mediator);
        Services.AddScoped<ToastService>();
        return Render<MiFirma>();
    }

    private static FirmaGuardadaUsuarioDto Firma(DateTime actualizadaEnUtc) =>
        new("firmas/firma.png", actualizadaEnUtc);

    private static string UrlEsperada(DateTime actualizadaEnUtc) => $"/mi-firma/archivo?v={actualizadaEnUtc.Ticks}";

    private static AngleSharp.Dom.IElement BotonGuardar(IRenderedComponent<MiFirma> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains("Guardar como mi firma"));

    private static AngleSharp.Dom.IElement PestanaEscribir(IRenderedComponent<MiFirma> cut) =>
        cut.FindAll("[role=tab]").Single(b => b.TextContent.Contains("Escribir mi nombre"));

    private IReadOnlyList<ToastMensaje> Toasts => Services.GetRequiredService<ToastService>().Mensajes;

    private int Llamadas(string identificador) => _modulo.Invocations.Count(i => i.Identifier == identificador);

    [Fact]
    public void Sin_firma_guardada_lo_dice_y_no_pinta_una_imagen_que_daria_404()
    {
        var cut = Renderizar(() => null);

        cut.Markup.Should().Contain("Aún no tienes ninguna firma guardada");
        cut.FindAll("img").Should().BeEmpty(
            "/mi-firma/archivo responde 404 sin firma: pintar el <img> sería mostrar una imagen rota");
    }

    [Fact]
    public void Con_firma_guardada_muestra_la_imagen_la_fecha_y_donde_se_usa()
    {
        var actualizada = new DateTime(2026, 9, 2, 15, 24, 0, DateTimeKind.Utc);
        var cut = Renderizar(() => Firma(actualizada));

        cut.Find("img").GetAttribute("src").Should().StartWith("/mi-firma/archivo");
        cut.Markup.Should().Contain(actualizada.ToLocalTime().ToString("dd/MM/yyyy HH:mm"));
        cut.Markup.Should().NotContain("Aún no tienes ninguna firma guardada");
        cut.Find("a[href='/documentos']").TextContent.Should().Contain("Ir a Documentos");
    }

    /// <summary>
    /// Antes, una excepción al cargar reventaba la página entera. Ahora se
    /// ofrece reintentar, y el lienzo NO se engancha mientras no existe: si se
    /// marcara como iniciado en el estado de error, al reintentar con éxito el
    /// &lt;canvas&gt; aparecería sin firmaEnCampo.js detrás y no se podría dibujar.
    /// </summary>
    [Fact]
    public async Task Si_la_carga_falla_ofrece_reintentar_y_el_lienzo_se_engancha_al_volver()
    {
        var cut = Renderizar(
            () => throw new InvalidOperationException("base caída"),
            () => null);

        cut.Markup.Should().Contain("No pudimos cargar tu firma");
        cut.FindAll("canvas").Should().BeEmpty();
        Llamadas("iniciar").Should().Be(0, "en el estado de error no hay lienzo al que engancharse");

        await cut.FindAll("button").Single(b => b.TextContent.Contains("Reintentar")).ClickAsync(new());

        cut.FindAll("canvas").Should().ContainSingle();
        cut.WaitForAssertion(() => Llamadas("iniciar").Should().Be(1,
            "al reaparecer el lienzo tiene que engancharse, o no se podría dibujar"));
    }

    /// <summary>
    /// En la ruta normal el lienzo se engancha una sola vez, por muchos
    /// renders que vengan después: un segundo <c>iniciar</c> duplicaría los
    /// listeners del &lt;canvas&gt; y cada trazo se registraría dos veces.
    /// </summary>
    [Fact]
    public async Task El_lienzo_se_engancha_exactamente_una_vez_aunque_haya_mas_renders()
    {
        var cut = Renderizar(() => null);
        cut.WaitForAssertion(() => Llamadas("iniciar").Should().Be(1));

        await PestanaEscribir(cut).ClickAsync(new());
        await cut.InvokeAsync(() => cut.Instance.MarcarTrazoIniciadoAsync());

        Llamadas("iniciar").Should().Be(1, "cada render posterior no debe volver a enganchar el lienzo");
    }

    /// <summary>
    /// La marca de «enganchado» se fija solo tras un <c>iniciar</c> correcto, así
    /// que mientras está en vuelo hace falta otra guarda: un render intermedio
    /// no puede lanzar un segundo enganche en paralelo.
    /// </summary>
    [Fact]
    public async Task Un_render_mientras_el_enganche_esta_en_vuelo_no_lanza_otro()
    {
        var iniciar = _modulo.SetupVoid("iniciar", _ => true);
        var cut = Renderizar(() => null);
        cut.WaitForAssertion(() => Llamadas("iniciar").Should().Be(1));

        await PestanaEscribir(cut).ClickAsync(new());
        Llamadas("iniciar").Should().Be(1, "el primer enganche aún no ha vuelto: lanzar otro duplicaría los listeners");

        iniciar.SetVoidResult();
        await cut.InvokeAsync(() => cut.Instance.MarcarTrazoIniciadoAsync());
        Llamadas("iniciar").Should().Be(1);
    }

    /// <summary>
    /// Hallazgo de revisión: la marca se fijaba ANTES de los await, así que un
    /// fallo transitorio de <c>iniciar</c> dejaba el lienzo sin listeners para
    /// siempre. Ahora el siguiente render lo reintenta, y tras el éxito ya no
    /// vuelve a intentarlo.
    /// </summary>
    [Fact]
    public async Task Si_iniciar_falla_el_siguiente_render_lo_reintenta()
    {
        var iniciar = _modulo.SetupVoid("iniciar", _ => true);
        iniciar.SetException(new JSException("fallo transitorio de firmaEnCampo.js"));
        var cut = Renderizar(() => null);
        cut.WaitForAssertion(() => Llamadas("iniciar").Should().Be(1));

        iniciar.SetVoidResult();
        await PestanaEscribir(cut).ClickAsync(new());

        cut.WaitForAssertion(() => Llamadas("iniciar").Should().Be(2,
            "tras un fallo de iniciar, el siguiente render tiene que volver a intentar el enganche"));

        await cut.InvokeAsync(() => cut.Instance.MarcarTrazoIniciadoAsync());
        Llamadas("iniciar").Should().Be(2, "una vez enganchado, no se vuelve a enganchar");
    }

    [Fact]
    public async Task Guardar_explica_por_que_esta_deshabilitado_hasta_que_hay_trazo()
    {
        var cut = Renderizar(() => null);

        BotonGuardar(cut).HasAttribute("disabled").Should().BeTrue();
        cut.Markup.Should().Contain("Dibuja un trazo o escribe tu nombre para poder guardar.");

        await cut.InvokeAsync(() => cut.Instance.MarcarTrazoIniciadoAsync());

        BotonGuardar(cut).HasAttribute("disabled").Should().BeFalse();
        cut.Markup.Should().NotContain("Dibuja un trazo o escribe tu nombre para poder guardar.",
            "con trazo ya se puede guardar: la pista mentiría");
    }

    [Fact]
    public async Task Escribir_mi_nombre_se_marca_como_pestana_activa_y_cambia_la_pista_del_lienzo()
    {
        var cut = Renderizar(() => null);
        var dibujar = () => cut.FindAll("[role=tab]").Single(b => b.TextContent.Contains("Dibujar"));

        dibujar().GetAttribute("aria-selected").Should().Be("true");
        cut.Find(".mi-firma-lienzo-pista").TextContent.Should().Contain("Dibuja aquí tu firma");

        await PestanaEscribir(cut).ClickAsync(new());

        PestanaEscribir(cut).GetAttribute("aria-selected").Should().Be("true");
        dibujar().GetAttribute("aria-selected").Should().Be("false");
        cut.Markup.Should().Contain("Tu nombre");
        cut.Find(".mi-firma-lienzo-pista").TextContent.Should().Contain("Escribe tu nombre arriba");
    }

    /// <summary>
    /// Defecto previo, encontrado al reconciliar: el &lt;img&gt; llevaba siempre
    /// <c>src="/mi-firma/archivo"</c>, así que al reemplazar la firma Blazor no
    /// tocaba el elemento y el navegador seguía enseñando la anterior. La URL
    /// lleva los ticks de <c>ActualizadaEnUtc</c>: cambia exactamente cuando
    /// cambia la firma, no en cada render.
    /// </summary>
    [Fact]
    public async Task Tras_guardar_la_imagen_cambia_de_url_para_no_mostrar_la_firma_anterior()
    {
        var antes = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var despues = new DateTime(2026, 9, 10, 9, 30, 0, DateTimeKind.Utc);
        _modulo.Setup<string?>("exportarPng", _ => true).SetResult(Convert.ToBase64String([1, 2, 3]));
        var cut = Renderizar(() => Firma(antes), () => Firma(despues));
        cut.Find("img").GetAttribute("src").Should().Be(UrlEsperada(antes));

        await cut.InvokeAsync(() => cut.Instance.MarcarTrazoIniciadoAsync());
        await BotonGuardar(cut).ClickAsync(new());

        _mediator.GuardadosRecibidos.Should().Be(1, "si no se guardó, el test no mide nada");
        cut.Find("img").GetAttribute("src").Should().Be(UrlEsperada(despues),
            "con la misma URL el navegador no vuelve a pedir la imagen y enseña la firma reemplazada");
        Toasts.Should().ContainSingle(t => t.Tono == TonoToast.Exito && t.Mensaje == "Firma guardada.");
    }

    /// <summary>
    /// Hallazgo de revisión: una excepción del comando no tenía catch. Ahora
    /// se avisa con un toast de error, y el lienzo conserva el trazo —el
    /// guardado no se confirmó, así que borrarlo obligaría a redibujar para
    /// reintentar—.
    /// </summary>
    [Fact]
    public async Task Si_el_guardado_lanza_se_avisa_y_el_lienzo_conserva_el_trazo()
    {
        var antes = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        _modulo.Setup<string?>("exportarPng", _ => true).SetResult(Convert.ToBase64String([1, 2, 3]));
        var cut = Renderizar(() => Firma(antes));
        _mediator.Guardar = () => throw new InvalidOperationException("almacenamiento caído");

        await cut.InvokeAsync(() => cut.Instance.MarcarTrazoIniciadoAsync());
        await BotonGuardar(cut).ClickAsync(new());

        _mediator.GuardadosRecibidos.Should().Be(1);
        Toasts.Should().ContainSingle(t => t.Tono == TonoToast.Error && t.Mensaje.StartsWith(MensajeFalloGuardado));
        Toasts.Should().NotContain(t => t.Tono == TonoToast.Exito);
        Llamadas("limpiar").Should().Be(0, "sin guardado confirmado no se borra el trazo");
        BotonGuardar(cut).HasAttribute("disabled").Should().BeFalse("el trazo sigue ahí: se puede reintentar sin redibujar");
        cut.Find("img").GetAttribute("src").Should().Be(UrlEsperada(antes));
    }

    /// <summary>
    /// Hallazgo de revisión: si el comando guardaba pero la recarga lanzaba, el
    /// lienzo ya estaba limpio y la tarjeta seguía enseñando la firma anterior
    /// como si fuera la vigente. Ahora se dice que se guardó pero no se pudo
    /// mostrar, la tarjeta lo advierte, y «Volver a cargar» lo resuelve.
    /// </summary>
    [Fact]
    public async Task Si_guarda_pero_falla_la_recarga_lo_dice_asi_y_permite_volver_a_cargar()
    {
        var antes = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var despues = new DateTime(2026, 9, 10, 9, 30, 0, DateTimeKind.Utc);
        _modulo.Setup<string?>("exportarPng", _ => true).SetResult(Convert.ToBase64String([1, 2, 3]));
        var cut = Renderizar(
            () => Firma(antes),
            () => throw new InvalidOperationException("base caída tras guardar"),
            () => Firma(despues));

        await cut.InvokeAsync(() => cut.Instance.MarcarTrazoIniciadoAsync());
        await BotonGuardar(cut).ClickAsync(new());

        _mediator.GuardadosRecibidos.Should().Be(1);
        Toasts.Should().ContainSingle(t => t.Tono == TonoToast.Advertencia && t.Mensaje.Contains("se guardó"));
        Toasts.Should().NotContain(t => t.Tono == TonoToast.Exito, "«Firma guardada.» a secas callaría que lo mostrado es la anterior");
        Toasts.Should().NotContain(t => t.Mensaje.StartsWith(MensajeFalloGuardado), "el guardado sí se hizo: decir lo contrario mentiría");
        Llamadas("limpiar").Should().Be(1, "el guardado se confirmó: el trazo ya no hace falta");
        cut.Find(".mi-firma-desactualizada").TextContent.Should().Contain("anterior al guardado");

        await cut.FindAll("button").Single(b => b.TextContent.Contains("Volver a cargar")).ClickAsync(new());

        cut.FindAll(".mi-firma-desactualizada").Should().BeEmpty();
        cut.Find("img").GetAttribute("src").Should().Be(UrlEsperada(despues));
    }

    /// <summary>
    /// Hallazgo de revisión: en la subida de archivo, un fallo al guardar caía
    /// en el mismo catch que la lectura y se informaba como «no se pudo leer
    /// el archivo», que manda al usuario a probar otra imagen cuando la imagen
    /// estaba bien.
    /// </summary>
    [Fact]
    public void Subir_un_archivo_si_falla_el_guardado_no_se_informa_como_fallo_de_lectura()
    {
        var cut = Renderizar(() => null);
        _mediator.Guardar = () => throw new InvalidOperationException("almacenamiento caído");

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "firma.png"));

        cut.WaitForAssertion(() => _mediator.GuardadosRecibidos.Should().Be(1));
        cut.WaitForAssertion(() => Toasts.Should().ContainSingle(t => t.Tono == TonoToast.Error && t.Mensaje.StartsWith(MensajeFalloGuardado)));
        Toasts.Should().NotContain(t => t.Mensaje.StartsWith(MensajeFalloLectura));
    }

    [Fact]
    public void Subir_un_archivo_que_no_se_puede_leer_lo_dice_y_no_intenta_guardar()
    {
        var cut = Renderizar(() => null);
        var demasiadoGrande = new byte[(5 * 1024 * 1024) + 1];

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(demasiadoGrande, "firma.png"));

        cut.WaitForAssertion(() => Toasts.Should().ContainSingle(t => t.Tono == TonoToast.Error && t.Mensaje.StartsWith(MensajeFalloLectura)));
        _mediator.GuardadosRecibidos.Should().Be(0);
    }
}
