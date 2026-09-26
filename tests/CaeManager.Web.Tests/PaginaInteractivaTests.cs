using Bunit;
using CaeManager.Application.Common;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E1b: una página InteractiveServer es la raíz de su circuito, así que lo que lanza la
/// PROPIA página —su OnInitializedAsync, un manejador suyo— no lo contiene ningún límite
/// colocado dentro de ella. <see cref="PaginaInteractiva"/> lo contiene y
/// <c>&lt;LimiteDeErrores Pagina="this"&gt;</c> muestra el aviso con reintento. "El circuito
/// sobrevive" se mide aquí como en bUnit se puede: el renderizador no recibe la excepción
/// (ni Render ni Click la relanzan) y la página sigue pintándose después. El control
/// negativo, la misma página sobre ComponentBase, demuestra que el arnés sí ve la caída.
/// </summary>
public class PaginaInteractivaTests : BunitContext
{
    private const string DetalleInterno = "Npgsql: tenant 7f3a… columna secreta";

    private readonly AlertaOperativaQueCuenta _alertas = new();

    public PaginaInteractivaTests()
    {
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddSingleton<IAlertaOperativa>(_alertas);
        Services.AddLocalization();
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    [Fact]
    public void Una_excepcion_en_el_OnInitializedAsync_de_la_pagina_muestra_el_aviso_con_reintento_y_no_tumba_el_circuito()
    {
        var cut = Render<PaginaQueFallaAlIniciar>();

        cut.WaitForAssertion(() => cut.Find("[data-limite-errores]"));
        ComprobarAvisoSinDetalles(cut);
        cut.FindAll("#contenido").Should().BeEmpty("el contenido de una página que no llegó a iniciarse no se pinta");
        _alertas.Capturadas.Should().ContainSingle().Which.Message.Should().Be(DetalleInterno,
            "la excepción, con todo su detalle, va a Sentry en vez de a la pantalla");
    }

    [Fact]
    public void Una_excepcion_sincrona_en_OnInitialized_tambien_queda_contenida()
    {
        var cut = Render<PaginaQueFallaAlIniciarSinEsperar>();

        ComprobarAvisoSinDetalles(cut);
        _alertas.Capturadas.Should().ContainSingle();
    }

    [Fact]
    public void Una_excepcion_en_un_manejador_de_la_pagina_muestra_el_aviso_con_reintento_y_no_tumba_el_circuito()
    {
        var cut = Render<PaginaQueFallaEnManejador>();
        cut.Find("#contenido").TextContent.Should().Be("Página cargada");

        var clic = () => cut.Find("#guardar").Click();

        clic.Should().NotThrow("el fallo del manejador lo contiene la página, no llega al circuito");
        cut.WaitForAssertion(() => cut.Find("[data-limite-errores]"));
        ComprobarAvisoSinDetalles(cut);
        _alertas.Capturadas.Should().ContainSingle().Which.Message.Should().Be(DetalleInterno);
    }

    [Fact]
    public void Una_excepcion_en_OnAfterRenderAsync_de_la_pagina_queda_contenida()
    {
        var cut = Render<PaginaQueFallaTrasPintar>();

        cut.WaitForAssertion(() => cut.Find("[data-limite-errores]"));
        ComprobarAvisoSinDetalles(cut);
        _alertas.Capturadas.Should().ContainSingle();
    }

    [Fact]
    public void Un_OnAfterRenderAsync_cancelado_no_es_un_fallo()
    {
        var cut = Render<PaginaCanceladaTrasPintar>();

        cut.WaitForState(() => cut.Instance.Terminado);
        cut.FindAll("[data-limite-errores]").Should().BeEmpty(
            "el renderizador de Blazor ignora una tarea cancelada tras pintar; la base no puede convertirla en fallo");
        cut.Find("#contenido").TextContent.Should().Be("Página cargada");
        _alertas.Capturadas.Should().BeEmpty("una cancelación no es un error que avisar a Sentry");
    }

    [Fact]
    public void Control_negativo_sin_PaginaInteractiva_el_mismo_fallo_del_manejador_llega_al_circuito()
    {
        var cut = Render<PaginaSinBaseQueFallaEnManejador>();

        var clic = () => cut.Find("#guardar").Click();

        clic.Should().Throw<InvalidOperationException>(
            "sin la base, el receptor del evento es la página y el límite interior no la cubre: en producción esto tumba el circuito");
    }

    [Fact]
    public void Una_redireccion_de_NavigationManager_no_se_contiene()
    {
        var render = () => Render<PaginaQueRedirigeLanzando>();

        render.Should().Throw<NavigationException>(
            "es como NavigationManager redirige en el prerenderizado; contenerla rompería la redirección");
        _alertas.Capturadas.Should().BeEmpty();
    }

    private void ComprobarAvisoSinDetalles<TPagina>(IRenderedComponent<TPagina> cut) where TPagina : IComponent
    {
        var aviso = cut.Find("[data-limite-errores]");
        aviso.GetAttribute("role").Should().Be("alert");
        aviso.TextContent.Should().Contain("Algo ha fallado; vuelve a intentarlo");
        aviso.TextContent.Should().NotContain(DetalleInterno, "el mensaje de la excepción puede llevar datos del Tenant propietario");
        aviso.TextContent.Should().NotContain(nameof(InvalidOperationException), "tampoco se enseña el tipo");

        var reintentar = cut.Find("[data-limite-errores] a");
        reintentar.GetAttribute("href").Should().Be(Services.GetRequiredService<NavigationManager>().Uri,
            "el estado de una página que falló no es de fiar: reintentar es volver a pedirla entera");
        reintentar.GetAttribute("data-enhance-nav").Should().Be("false");
        reintentar.HasAttribute("onclick").Should().BeFalse("la CSP (script-src 'self') bloquea los manejadores inline");
    }

    /// <summary>Marcado de página con el envoltorio, como lo exige el trinquete de Architecture.Tests.</summary>
    private static void PintarEnvuelta(RenderTreeBuilder builder, PaginaInteractiva? pagina, RenderFragment contenido)
    {
        builder.OpenComponent<LimiteDeErrores>(0);
        builder.AddAttribute(1, nameof(LimiteDeErrores.Pagina), pagina);
        builder.AddAttribute(2, nameof(LimiteDeErrores.ChildContent), contenido);
        builder.CloseComponent();
    }

    private static RenderFragment Contenido(ComponentBase pagina, Action guardar) => b =>
    {
        b.OpenElement(0, "p");
        b.AddAttribute(1, "id", "contenido");
        b.AddContent(2, "Página cargada");
        b.CloseElement();
        b.OpenElement(3, "button");
        b.AddAttribute(4, "id", "guardar");
        b.AddAttribute(5, "onclick", EventCallback.Factory.Create(pagina, guardar));
        b.AddContent(6, "Guardar");
        b.CloseElement();
    };

    private sealed class PaginaQueFallaAlIniciar : PaginaInteractiva
    {
        protected override async Task OnInitializedAsync()
        {
            await Task.Yield();
            throw new InvalidOperationException(DetalleInterno);
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            PintarEnvuelta(builder, this, Contenido(this, () => { }));
    }

    private sealed class PaginaQueFallaAlIniciarSinEsperar : PaginaInteractiva
    {
        protected override void OnInitialized() => throw new InvalidOperationException(DetalleInterno);

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            PintarEnvuelta(builder, this, Contenido(this, () => { }));
    }

    private sealed class PaginaQueFallaEnManejador : PaginaInteractiva
    {
        private void Guardar() => throw new InvalidOperationException(DetalleInterno);

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            PintarEnvuelta(builder, this, Contenido(this, Guardar));
    }

    private sealed class PaginaQueFallaTrasPintar : PaginaInteractiva
    {
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            await Task.Yield();
            throw new InvalidOperationException(DetalleInterno);
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            PintarEnvuelta(builder, this, Contenido(this, () => { }));
    }

    private sealed class PaginaCanceladaTrasPintar : PaginaInteractiva
    {
        public bool Terminado { get; private set; }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            using var cancelacion = new CancellationTokenSource();
            await cancelacion.CancelAsync();
            try
            {
                await Task.Delay(1000, cancelacion.Token);
            }
            finally
            {
                Terminado = true;
            }
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            PintarEnvuelta(builder, this, Contenido(this, () => { }));
    }

    private sealed class PaginaSinBaseQueFallaEnManejador : ComponentBase
    {
        private void Guardar() => throw new InvalidOperationException(DetalleInterno);

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            PintarEnvuelta(builder, null, Contenido(this, Guardar));
    }

    private sealed class PaginaQueRedirigeLanzando : PaginaInteractiva
    {
        protected override void OnInitialized() => throw new NavigationException("/otra");

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            PintarEnvuelta(builder, this, Contenido(this, () => { }));
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
