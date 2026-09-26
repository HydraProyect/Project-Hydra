using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Opciones = Microsoft.Extensions.Options.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E1: una excepción no controlada dentro de una región se queda en su
/// <see cref="LimiteDeErrores"/> — aviso recuperable, registro en Sentry, sin
/// detalles internos — y el resto de la pantalla sigue viva.
///
/// <para>
/// Qué NO demuestran estos tests: que una página InteractiveServer quede cubierta
/// por el límite de MainLayout. bUnit monta layout y página en un solo renderizador;
/// en producción una página con su propio @rendermode es raíz de su circuito y el
/// layout se pinta en estático, así que ese límite solo la cubre en el
/// prerenderizado. Esas páginas llevan su propio envoltorio, probado en
/// <see cref="PaginaInteractivaTests"/> (P1-E1b).
/// </para>
/// </summary>
public class LimiteDeErroresTests : BunitContext
{
    private const string DetalleInterno = "Npgsql: tenant 7f3a… columna secreta";

    private readonly AlertaOperativaQueCuenta _alertas = new();

    public LimiteDeErroresTests()
    {
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddSingleton<IAlertaOperativa>(_alertas);
        Services.AddLocalization();
    }

    [Fact]
    public void Un_hijo_que_lanza_muestra_el_aviso_recuperable_y_el_resto_de_la_pantalla_sigue_viva()
    {
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        var cut = Render<Anfitrion>(p => p.Add(a => a.Hijo, typeof(LanzaAlPintar)));

        var aviso = cut.Find("[data-limite-errores]");
        aviso.GetAttribute("role").Should().Be("alert");
        aviso.TextContent.Should().Contain("Algo ha fallado; vuelve a intentarlo");
        aviso.TextContent.Should().NotContain(DetalleInterno, "el mensaje de la excepción puede llevar datos del Tenant propietario");
        aviso.TextContent.Should().NotContain(nameof(InvalidOperationException), "tampoco se enseña el tipo de la excepción");

        cut.Find("#hermano").TextContent.Should().Be("Sigo aquí", "el fallo se queda dentro de su región");
        _alertas.Capturadas.Should().ContainSingle().Which.Message.Should().Be(DetalleInterno,
            "la excepción, con todo su detalle, va a Sentry en vez de a la pantalla");
    }

    [Fact]
    public void Una_excepcion_en_el_ciclo_de_vida_de_un_hijo_tambien_queda_contenida()
    {
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        var cut = Render<Anfitrion>(p => p.Add(a => a.Hijo, typeof(LanzaAlIniciar)));

        cut.WaitForAssertion(() => cut.Find("[data-limite-errores]"));
        cut.Find("#hermano").TextContent.Should().Be("Sigo aquí");
    }

    [Fact]
    public void Reintentar_vuelve_a_pintar_la_region_y_si_ya_no_falla_desaparece_el_aviso()
    {
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        FallaUnaVez.Pendiente = true;
        var cut = Render<Anfitrion>(p => p.Add(a => a.Hijo, typeof(FallaUnaVez)));
        cut.Find("[data-limite-errores]");

        cut.Find("[data-limite-errores] button").Click();

        cut.FindAll("[data-limite-errores]").Should().BeEmpty("el reintento vuelve a montar el contenido");
        cut.Find("#recuperado").TextContent.Should().Be("Contenido cargado");
    }

    [Fact]
    public void En_MainLayout_un_fallo_de_la_pagina_se_queda_en_main_y_la_barra_lateral_y_la_cabecera_siguen()
    {
        ComponentFactories.Add(new SoloLoQueSeMide());
        var usuarios = CrearUsuariosSinAlmacen();
        Services.AddSingleton(new EstadoDelCircuito());
        Services.AddSingleton(new PuertaAccesoDatos());
        Services.AddSingleton(usuarios);
        Services.AddSingleton(new ActividadUsuarioService(null!, usuarios, new PuertaAccesoDatos()));
        Services.AddSingleton<AuthenticationStateProvider>(new ProveedorAnonimo());
        Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());

        // MainLayout se pinta en estático en producción.
        SetRendererInfo(new RendererInfo("Static", isInteractive: false));
        RenderFragment pagina = b => { b.OpenComponent<LanzaAlPintar>(0); b.CloseComponent(); };
        var cut = Render<MainLayout>(p => p.Add(l => l.Body, pagina));

        cut.Find("main.contenido [data-limite-errores]").TextContent.Should().Contain("Algo ha fallado; vuelve a intentarlo");
        cut.Find("aside.barra-lateral").Should().NotBeNull("la navegación no se pierde");
        cut.Find("header.barra-superior").Should().NotBeNull();
        _alertas.Capturadas.Should().ContainSingle();
    }

    /// <summary>
    /// El límite de MainLayout se pinta en estático: ahí no hay circuito que reciba
    /// un @onclick, así que Recover no haría nada. El reintento es recargar la página
    /// (dos hallazgos de la revisión de Codex: sin circuito y con CSP).
    /// </summary>
    [Fact]
    public void Pintado_en_estatico_Reintentar_recarga_la_pagina_en_vez_de_depender_de_un_circuito()
    {
        SetRendererInfo(new RendererInfo("Static", isInteractive: false));

        var cut = Render<Anfitrion>(p => p.Add(a => a.Hijo, typeof(LanzaAlPintar)));

        var reintentar = cut.Find("[data-limite-errores] a");
        reintentar.GetAttribute("href").Should().Be(Services.GetRequiredService<NavigationManager>().Uri,
            "reintentar es volver a pedir la misma página");
        reintentar.GetAttribute("data-enhance-nav").Should().Be("false", "petición completa, no navegación mejorada");
        reintentar.HasAttribute("onclick").Should().BeFalse("la CSP (script-src 'self') bloquea los manejadores inline");
    }

    /// <summary>
    /// ContextWorkspace sí es un árbol interactivo de verdad en producción (raíz
    /// propia en MainLayout, con sus paneles 360 como hijos): aquí el límite cubre
    /// también el ciclo de vida y los eventos de los paneles.
    /// </summary>
    [Fact]
    public async Task En_el_Context_Workspace_un_panel_360_que_lanza_no_se_lleva_la_cabecera_ni_el_boton_de_cerrar()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        ComponentFactories.Add(new PanelEmpresaQueLanza());
        var workspace = new ContextWorkspaceService();
        Services.AddSingleton(workspace);
        Services.AddSingleton(new ToastService());

        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        var cut = Render<ContextWorkspace>();
        await cut.InvokeAsync(() => workspace.AbrirAsync(EntidadWorkspace.Empresa, Guid.NewGuid(), "Empresa de prueba", "resumen"));

        cut.WaitForAssertion(() => cut.Find(".workspace-cuerpo [data-limite-errores]"));
        cut.Find("button.workspace-cerrar").Should().NotBeNull("el usuario puede cerrar el panel que ha fallado");
        _alertas.Capturadas.Should().ContainSingle();
    }

    private sealed class PanelEmpresaQueLanza : IComponentFactory
    {
        public bool CanCreate(Type componentType) =>
            componentType == typeof(CaeManager.Web.Features.Empresas.Components.EmpresaWorkspacePanel);

        public IComponent Create(Type componentType) => new PanelQueLanza();

        private sealed class PanelQueLanza : ComponentBase
        {
            [Parameter(CaptureUnmatchedValues = true)] public Dictionary<string, object>? Resto { get; set; }

            protected override void BuildRenderTree(RenderTreeBuilder builder) =>
                throw new InvalidOperationException(DetalleInterno);
        }
    }

    /// <summary>Una región con un hermano fuera del límite, para ver que sigue vivo.</summary>
    private sealed class Anfitrion : ComponentBase
    {
        [Parameter] public Type Hijo { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "p");
            builder.AddAttribute(1, "id", "hermano");
            builder.AddContent(2, "Sigo aquí");
            builder.CloseElement();
            builder.OpenComponent<LimiteDeErrores>(3);
            builder.AddAttribute(4, nameof(LimiteDeErrores.ChildContent), (RenderFragment)(b =>
            {
                b.OpenComponent(0, Hijo);
                b.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    private sealed class LanzaAlPintar : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            throw new InvalidOperationException(DetalleInterno);
    }

    private sealed class LanzaAlIniciar : ComponentBase
    {
        protected override async Task OnInitializedAsync()
        {
            await Task.Yield();
            throw new InvalidOperationException(DetalleInterno);
        }
    }

    private sealed class FallaUnaVez : ComponentBase
    {
        public static bool Pendiente;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (Pendiente)
            {
                Pendiente = false;
                throw new InvalidOperationException(DetalleInterno);
            }

            builder.OpenElement(0, "p");
            builder.AddAttribute(1, "id", "recuperado");
            builder.AddContent(2, "Contenido cargado");
            builder.CloseElement();
        }
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

    /// <summary>
    /// Monta de verdad el layout, el límite y lo que pinta su aviso; el resto del
    /// layout (NavMenu, selectores, avisos de soporte…) queda como stub.
    /// </summary>
    private sealed class SoloLoQueSeMide : IComponentFactory
    {
        private static readonly HashSet<Type> Reales =
        [
            typeof(MainLayout), typeof(LimiteDeErrores), typeof(LanzaAlPintar),
            typeof(EstadoVacio), typeof(Boton), typeof(Icono),
        ];

        public bool CanCreate(Type componentType) => !Reales.Contains(componentType);

        public IComponent Create(Type componentType) =>
            (IComponent)Activator.CreateInstance(typeof(Stub<>).MakeGenericType(componentType))!;
    }

    private static UserManager<ApplicationUser> CrearUsuariosSinAlmacen() => new(
        new AlmacenSinUso(), Opciones.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(),
        [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
        NullLogger<UserManager<ApplicationUser>>.Instance);

    private sealed class ProveedorAnonimo : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    private sealed class AlmacenSinUso : IUserStore<ApplicationUser>
    {
        public void Dispose() { }
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct) => throw new NotSupportedException();
    }
}
