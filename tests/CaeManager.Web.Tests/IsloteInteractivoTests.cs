using Bunit;
using CaeManager.Application.Common;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E1c: un islote interactivo (cada componente de MainLayout con
/// <c>@rendermode="InteractiveServer"</c>, cada componente sin ruta con <c>@rendermode</c>
/// propio) es otra raíz del circuito de la página. <see cref="IsloteInteractivo"/> contiene
/// lo que lanza el propio islote y <c>&lt;LimiteDeErrores Islote="this"&gt;</c> pinta el aviso
/// compacto, que cabe en una cabecera. Como en <see cref="PaginaInteractivaTests"/>, "el
/// circuito sobrevive" se mide como en bUnit se puede: el renderizador no recibe la excepción.
/// </summary>
public class IsloteInteractivoTests : BunitContext
{
    private const string DetalleInterno = "Npgsql: tenant 7f3a… columna secreta";

    private readonly AlertaOperativaQueCuenta _alertas = new();

    public IsloteInteractivoTests()
    {
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddSingleton<IAlertaOperativa>(_alertas);
        Services.AddLocalization();
        // Solo lo consume SesionDelCircuito, el islote real del último test.
        Services.AddSingleton<AuthenticationStateProvider>(new ProveedorQueFalla());
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    [Fact]
    public void Una_excepcion_en_el_OnInitializedAsync_del_islote_muestra_el_aviso_compacto_y_no_tumba_el_circuito()
    {
        var cut = Render<IsloteQueFallaAlIniciar>();

        cut.WaitForAssertion(() => cut.Find("[data-limite-errores-compacto]"));
        ComprobarAvisoCompactoConRecarga(cut);
        cut.FindAll("#contenido").Should().BeEmpty();
        _alertas.Capturadas.Should().ContainSingle().Which.Message.Should().Be(DetalleInterno,
            "la excepción, con todo su detalle, va a Sentry en vez de a la pantalla");
    }

    [Fact]
    public void Una_excepcion_en_un_manejador_del_islote_muestra_el_aviso_compacto_y_no_tumba_el_circuito()
    {
        var cut = Render<IsloteQueFallaEnManejador>();
        cut.Find("#contenido").TextContent.Should().Be("Islote cargado");

        var clic = () => cut.Find("#accion").Click();

        clic.Should().NotThrow("el fallo del manejador lo contiene el islote, no llega al circuito");
        cut.WaitForAssertion(() => cut.Find("[data-limite-errores-compacto]"));
        ComprobarAvisoCompactoConRecarga(cut);
        _alertas.Capturadas.Should().ContainSingle();
    }

    [Fact]
    public void Una_excepcion_en_OnAfterRenderAsync_del_islote_queda_contenida()
    {
        var cut = Render<IsloteQueFallaTrasPintar>();

        cut.WaitForAssertion(() => cut.Find("[data-limite-errores-compacto]"));
        ComprobarAvisoCompactoConRecarga(cut);
        _alertas.Capturadas.Should().ContainSingle();
    }

    [Fact]
    public void Un_hijo_que_falla_al_pintar_muestra_el_aviso_compacto_con_reintento_en_el_circuito()
    {
        var cut = Render<IsloteConHijoQueFalla>();

        var aviso = cut.Find("[data-limite-errores-compacto]");
        aviso.TextContent.Should().NotContain(DetalleInterno);
        cut.FindAll("[data-limite-errores] a").Should().BeEmpty(
            "si el islote sigue sano solo cayó su contenido: el reintento es Recover, sin recargar la pantalla");
        cut.Find("[data-limite-errores] button").TextContent.Should().Contain("Reintentar");
        _alertas.Capturadas.Should().ContainSingle();
    }

    [Fact]
    public void Control_negativo_sin_IsloteInteractivo_el_mismo_fallo_del_manejador_llega_al_circuito()
    {
        var cut = Render<IsloteSinBaseQueFallaEnManejador>();

        var clic = () => cut.Find("#accion").Click();

        clic.Should().Throw<InvalidOperationException>(
            "sin la base, el receptor del evento es el islote y el límite interior no lo cubre: en producción esto tumba la pantalla");
    }

    [Fact]
    public void Un_islote_real_de_MainLayout_que_falla_al_iniciar_no_tumba_el_circuito()
    {
        IRenderedComponent<SesionDelCircuito>? cut = null;
        var render = () => cut = Render<SesionDelCircuito>();

        render.Should().NotThrow("SesionDelCircuito es IsloteInteractivo: el fallo de su OnInitializedAsync se contiene");
        cut!.WaitForAssertion(() => cut.Find("[data-limite-errores-compacto]"));
        _alertas.Capturadas.Should().ContainSingle();
        cut.Markup.Should().NotContain(DetalleInterno);
    }

    private void ComprobarAvisoCompactoConRecarga<T>(IRenderedComponent<T> cut) where T : IComponent
    {
        var aviso = cut.Find("[data-limite-errores-compacto]");
        aviso.GetAttribute("role").Should().Be("alert");
        aviso.ClassList.Should().Contain("limite-errores-compacto");
        aviso.TextContent.Should().Contain("No disponible");
        aviso.TextContent.Should().NotContain(DetalleInterno, "el mensaje de la excepción puede llevar datos del Tenant propietario");
        aviso.TextContent.Should().NotContain(nameof(InvalidOperationException), "tampoco se enseña el tipo");
        cut.FindAll("[data-limite-errores] .estado-vacio").Should().BeEmpty("en un islote el aviso es compacto, no el de región");

        var reintentar = cut.Find("[data-limite-errores] a");
        reintentar.GetAttribute("href").Should().Be(Services.GetRequiredService<NavigationManager>().Uri,
            "el estado de un islote que falló no es de fiar: reintentar es volver a pedir la pantalla");
        reintentar.GetAttribute("data-enhance-nav").Should().Be("false");
        reintentar.HasAttribute("onclick").Should().BeFalse("la CSP (script-src 'self') bloquea los manejadores inline");
    }

    private static void PintarEnvuelto(RenderTreeBuilder builder, IsloteInteractivo? islote, RenderFragment contenido)
    {
        builder.OpenComponent<LimiteDeErrores>(0);
        builder.AddAttribute(1, nameof(LimiteDeErrores.Islote), islote);
        builder.AddAttribute(2, nameof(LimiteDeErrores.ChildContent), contenido);
        builder.CloseComponent();
    }

    private static RenderFragment Contenido(ComponentBase islote, Action accion) => b =>
    {
        b.OpenElement(0, "span");
        b.AddAttribute(1, "id", "contenido");
        b.AddContent(2, "Islote cargado");
        b.CloseElement();
        b.OpenElement(3, "button");
        b.AddAttribute(4, "id", "accion");
        b.AddAttribute(5, "onclick", EventCallback.Factory.Create(islote, accion));
        b.AddContent(6, "Acción");
        b.CloseElement();
    };

    private sealed class IsloteQueFallaAlIniciar : IsloteInteractivo
    {
        protected override async Task OnInitializedAsync()
        {
            await Task.Yield();
            throw new InvalidOperationException(DetalleInterno);
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            PintarEnvuelto(builder, this, Contenido(this, () => { }));
    }

    private sealed class IsloteQueFallaEnManejador : IsloteInteractivo
    {
        private void Accion() => throw new InvalidOperationException(DetalleInterno);

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            PintarEnvuelto(builder, this, Contenido(this, Accion));
    }

    private sealed class IsloteQueFallaTrasPintar : IsloteInteractivo
    {
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            await Task.Yield();
            throw new InvalidOperationException(DetalleInterno);
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            PintarEnvuelto(builder, this, Contenido(this, () => { }));
    }

    private sealed class IsloteConHijoQueFalla : IsloteInteractivo
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            PintarEnvuelto(builder, this, b =>
            {
                b.OpenComponent<HijoQueFallaAlPintar>(0);
                b.CloseComponent();
            });
    }

    private sealed class HijoQueFallaAlPintar : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            throw new InvalidOperationException(DetalleInterno);
    }

    private sealed class IsloteSinBaseQueFallaEnManejador : ComponentBase
    {
        private void Accion() => throw new InvalidOperationException(DetalleInterno);

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            PintarEnvuelto(builder, null, Contenido(this, Accion));
    }

    private sealed class ProveedorQueFalla : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromException<AuthenticationState>(new InvalidOperationException(DetalleInterno));
    }

    private sealed class AlertaOperativaQueCuenta : IAlertaOperativa
    {
        public List<Exception> Capturadas { get; } = [];
        public void Emitir(string mensaje, NivelAlertaOperativa nivel) { }
        public void CapturarExcepcion(Exception excepcion) => Capturadas.Add(excepcion);
        public void DejarMigaDePan(string mensaje) { }
        public IDisposable IniciarAmbitoDeCaptura() => new Nada();
        private sealed class Nada : IDisposable { public void Dispose() { } }
    }
}
