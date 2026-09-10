using Bunit;
using CaeManager.Application.Documentos.Commands.GuardarFirmaGuardadaUsuario;
using CaeManager.Application.Documentos.Queries.ObtenerFirmaGuardadaUsuario;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Usuarios.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// /mi-firma en su versión Gen 2. Lo que se prueba aquí es lo que la pantalla
/// hace de nuevo o de distinto respecto a la versión anterior —estado sin
/// firma, estado de error con «Reintentar», pista del botón Guardar y la URL
/// de la imagen que cambia al guardar—, no el lienzo en sí, que vive en
/// firmaEnCampo.js y no se ejecuta bajo bUnit.
/// </summary>
public class MiFirmaTests : BunitContext
{
    private const string Modulo = "./js/firmaEnCampo.js";

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
                    return Task.FromResult((TResponse)(object)Result.Exito());
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

    private static AngleSharp.Dom.IElement BotonGuardar(IRenderedComponent<MiFirma> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains("Guardar como mi firma"));

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
        _modulo.Invocations.Should().NotContain(i => i.Identifier == "iniciar",
            "en el estado de error no hay lienzo al que engancharse");

        await cut.FindAll("button").Single(b => b.TextContent.Contains("Reintentar")).ClickAsync(new());

        cut.FindAll("canvas").Should().ContainSingle();
        cut.WaitForAssertion(() => _modulo.Invocations.Should().Contain(i => i.Identifier == "iniciar",
            "al reaparecer el lienzo tiene que engancharse, o no se podría dibujar"));
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
        var escribir = () => cut.FindAll("[role=tab]").Single(b => b.TextContent.Contains("Escribir mi nombre"));

        dibujar().GetAttribute("aria-selected").Should().Be("true");
        cut.Find(".mi-firma-lienzo-pista").TextContent.Should().Contain("Dibuja aquí tu firma");

        await escribir().ClickAsync(new());

        escribir().GetAttribute("aria-selected").Should().Be("true");
        dibujar().GetAttribute("aria-selected").Should().Be("false");
        cut.Markup.Should().Contain("Tu nombre");
        cut.Find(".mi-firma-lienzo-pista").TextContent.Should().Contain("Escribe tu nombre arriba");
    }

    /// <summary>
    /// Defecto previo, encontrado al reconciliar: el &lt;img&gt; llevaba siempre
    /// <c>src="/mi-firma/archivo"</c>, así que al reemplazar la firma Blazor no
    /// tocaba el elemento y el navegador seguía enseñando la anterior.
    /// </summary>
    [Fact]
    public async Task Tras_guardar_la_imagen_cambia_de_url_para_no_mostrar_la_firma_anterior()
    {
        var antes = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var despues = new DateTime(2026, 9, 10, 9, 30, 0, DateTimeKind.Utc);
        _modulo.Setup<string?>("exportarPng", _ => true).SetResult(Convert.ToBase64String([1, 2, 3]));
        var cut = Renderizar(() => Firma(antes), () => Firma(despues));
        var srcAntes = cut.Find("img").GetAttribute("src");

        await cut.InvokeAsync(() => cut.Instance.MarcarTrazoIniciadoAsync());
        await BotonGuardar(cut).ClickAsync(new());

        _mediator.GuardadosRecibidos.Should().Be(1, "si no se guardó, el test no mide nada");
        cut.Find("img").GetAttribute("src").Should().NotBe(srcAntes,
            "con la misma URL el navegador no vuelve a pedir la imagen y enseña la firma reemplazada");
    }
}
