using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Services;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Selector de empresa gestionada (Tenant beneficiario) de la cabecera de la barra
/// lateral: mockup «Selector Empresa Gestionada» y decisiones 1, 7 bis y 7 ter del
/// contrato. Lo que se prueba es lo que decide la pantalla —cuándo aparece, qué
/// lista, qué marca como activo, a dónde vuelve—; que el POST autorice o no lo
/// prueba la capa de integración con RLS, no esta.
/// </summary>
public class SelectorClienteActivoTests : BunitContext
{
    private static readonly Guid Origen = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid Norte = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Sur = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");

    private sealed class AntiforgeryFalso : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("token-de-prueba", "__RequestVerificationToken");
    }

    private sealed class Mediador(Func<IReadOnlyList<ClienteAutorizadoDto>> lista) : IMediator
    {
        public Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default) =>
            request is ObtenerClientesAutorizadosQuery
                ? Task.FromResult((T)(object)lista())
                : throw new NotSupportedException(request.GetType().Name);

        public Task Send<T>(T request, CancellationToken cancellationToken = default) where T : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<T>(T notification, CancellationToken cancellationToken = default) where T : INotification => Task.CompletedTask;
    }

    private IReadOnlyList<ClienteAutorizadoDto> _lista = [];

    public SelectorClienteActivoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => new Mediador(() => _lista));
        Services.AddScoped<AntiforgeryStateProvider, AntiforgeryFalso>();
        Services.AddSingleton<Microsoft.Extensions.Logging.ILogger<ExcepcionDeCircuitoDesconectado>>(
            NullLogger<ExcepcionDeCircuitoDesconectado>.Instance);
    }

    private static ClienteAutorizadoDto Propio(bool gestionado = false) =>
        new(Origen, "Operador de prueba", EsOrigen: true, EsGestionadoPorOperacion: gestionado);

    private static ClienteAutorizadoDto Gestionado(Guid id, string nombre) =>
        new(id, nombre, EsOrigen: false, EsGestionadoPorOperacion: true);

    private IRenderedComponent<SelectorClienteActivo> Renderizar(Guid? seleccionado = null, string url = "trabajadores")
    {
        Services.AddScoped<IClienteActivoSeleccionado>(_ => new SeleccionEmpresaGestionadaDePrueba(seleccionado));
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        return Render<SelectorClienteActivo>();
    }

    private static async Task Abrir(IRenderedComponent<SelectorClienteActivo> cut) =>
        await cut.Find(".selector-tenant-disparador").ClickAsync(new MouseEventArgs());

    private static List<string> NombresDeLaLista(IRenderedComponent<SelectorClienteActivo> cut) =>
        cut.FindAll(".selector-tenant-lista .selector-tenant-fila .selector-tenant-nombre").Select(n => n.TextContent.Trim()).ToList();

    // --- Visibilidad (decisión 1, evaluada por condición) ---------------------------------------

    [Fact]
    public void Un_usuario_mono_Tenant_no_ve_el_selector()
    {
        _lista = [Propio()];

        var cut = Renderizar();

        cut.FindAll(".selector-tenant").Should().BeEmpty();
        cut.Markup.Should().NotContain("Empresa gestionada");
    }

    [Fact]
    public void Con_un_unico_Tenant_de_cartera_y_el_origen_sin_gestionar_el_selector_sigue_oculto()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte")];

        Renderizar(seleccionado: Norte).FindAll(".selector-tenant").Should().BeEmpty();
    }

    [Fact]
    public void Con_varias_empresas_gestionadas_el_selector_es_visible_y_dice_cual_esta_activa()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];

        var cut = Renderizar(seleccionado: Sur);

        var disparador = cut.Find(".selector-tenant-disparador");
        disparador.GetAttribute("data-tenant-id").Should().Be(Sur.ToString());
        disparador.GetAttribute("aria-label").Should().Be("Contexto operativo actual: Empresa Sur");
        disparador.TextContent.Should().Contain("Empresa gestionada").And.Contain("Empresa Sur");
        disparador.GetAttribute("aria-expanded").Should().Be("false");
        cut.FindAll(".selector-tenant-panel").Should().BeEmpty("la lista está cerrada hasta pulsar el disparador");
    }

    [Fact]
    public void Sin_seleccion_el_activo_es_el_Tenant_de_origen()
    {
        _lista = [Propio(gestionado: true), Gestionado(Norte, "Empresa Norte")];

        Renderizar().Find(".selector-tenant-disparador").GetAttribute("data-tenant-id").Should().Be(Origen.ToString());
    }

    [Fact]
    public void Quien_solo_alcanza_un_Tenant_por_la_via_heredada_conserva_el_selector()
    {
        _lista = [Propio(), new ClienteAutorizadoDto(Sur, "Delegante", EsOrigen: false)];

        Renderizar().FindAll(".selector-tenant").Should().ContainSingle();
    }

    // --- Lista ---------------------------------------------------------------------------------

    [Fact]
    public async Task Al_abrir_muestra_la_cartera_con_su_cuenta_el_activo_marcado_y_el_origen_fijo_debajo()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var cut = Renderizar(seleccionado: Norte);

        await Abrir(cut);

        cut.Find(".selector-tenant-cabecera").TextContent.Should().Be("Mi cartera · 2 empresas");
        NombresDeLaLista(cut).Should().Equal("Empresa Norte", "Empresa Sur");
        var opciones = cut.FindAll("[role=option]");
        opciones.Should().HaveCount(3, "dos de la cartera y «Tu organización» debajo");
        opciones.Single(o => o.GetAttribute("aria-selected") == "true").GetAttribute("value").Should().Be(Norte.ToString());
        var origen = opciones.Last();
        origen.GetAttribute("value").Should().Be(Origen.ToString());
        origen.TextContent.Should().Contain("Tu organización");
        cut.Find(".selector-tenant-disparador").GetAttribute("aria-expanded").Should().Be("true");
    }

    [Fact]
    public async Task Un_Tenant_cuya_Asignacion_caduco_deja_de_listarse_y_la_seleccion_vuelve_al_origen()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var cut = Renderizar(seleccionado: Sur);
        await Abrir(cut);
        NombresDeLaLista(cut).Should().Contain("Empresa Sur");

        // La query ya no devuelve el Tenant caducado: otra pantalla nueva del selector.
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte")];
        var refrescado = Render<SelectorClienteActivo>();

        // Su selección (Sur) ya no está autorizada: el contexto efectivo vuelve al origen y, como aún
        // alcanza a Norte, el control se mantiene para que pueda elegirlo.
        refrescado.Find(".selector-tenant-disparador").GetAttribute("data-tenant-id").Should().Be(Origen.ToString());
        await refrescado.Find(".selector-tenant-disparador").ClickAsync(new MouseEventArgs());
        refrescado.Markup.Should().Contain("Empresa Norte").And.NotContain("Empresa Sur");
    }

    [Fact]
    public void Con_un_unico_Tenant_de_cartera_el_selector_reaparece_si_el_contexto_efectivo_es_el_origen()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte")];

        Renderizar(seleccionado: null).FindAll(".selector-tenant").Should().ContainSingle(
            "volvió al origen a propósito y la preferencia impide el Tenant por defecto");
    }

    [Fact]
    public async Task Cada_opcion_envia_su_Tenant_por_POST_al_endpoint_con_antiforgery_y_vuelve_a_la_ruta_sin_query()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var cut = Renderizar(url: "trabajadores?q=ana&estado=Vencido&empresaId=1234");

        await Abrir(cut);

        var formulario = cut.Find(".selector-tenant-panel form");
        formulario.GetAttribute("method").Should().Be("post");
        formulario.GetAttribute("action").Should().Be("/cuenta/cliente-activo");
        formulario.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value").Should().Be("token-de-prueba");
        formulario.QuerySelector("input[name=returnUrl]")!.GetAttribute("value").Should().Be("/trabajadores",
            "los filtros con Ids del Tenant anterior no sobreviven al cambio (I14)");
        var botones = formulario.QuerySelectorAll("button[type=submit][name=tenantId]");
        botones.Select(b => b.GetAttribute("value")).Should().BeEquivalentTo(
            new[] { Norte, Sur, Origen }.Select(id => id.ToString()));
    }

    [Fact]
    public async Task La_ruta_de_retorno_sigue_a_la_navegacion_del_circuito()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var cut = Renderizar(url: "trabajadores");

        Services.GetRequiredService<NavigationManager>().NavigateTo("centros?q=x");
        await Abrir(cut);

        cut.Find("input[name=returnUrl]").GetAttribute("value").Should().Be("/centros",
            "el selector vive en el layout y sobrevive a las navegaciones: no vuelve a la ruta con la que arrancó");
    }

    [Fact]
    public async Task Escape_cierra_la_lista()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var cut = Renderizar();
        await Abrir(cut);
        cut.FindAll(".selector-tenant-panel").Should().ContainSingle();

        await cut.Find(".selector-tenant").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        cut.FindAll(".selector-tenant-panel").Should().BeEmpty();
    }

    [Fact]
    public async Task Navegar_cierra_la_lista_abierta()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var cut = Renderizar();
        await Abrir(cut);

        Services.GetRequiredService<NavigationManager>().NavigateTo("centros");

        cut.WaitForAssertion(() => cut.FindAll(".selector-tenant-panel").Should().BeEmpty());
    }

    // --- Búsqueda (15 o más empresas) ------------------------------------------------------------

    private static List<ClienteAutorizadoDto> Cartera(int n) =>
        Enumerable.Range(1, n)
            .Select(i => Gestionado(Guid.Parse($"cccccccc-0000-0000-0000-{i:D12}"), i == 3 ? "Nervión Instalaciones" : $"Empresa {i:D2}"))
            .ToList();

    [Fact]
    public async Task Con_catorce_empresas_no_hay_buscador()
    {
        _lista = [Propio(), .. Cartera(14)];
        var cut = Renderizar();

        await Abrir(cut);

        cut.FindAll(".selector-tenant-busqueda").Should().BeEmpty();
        NombresDeLaLista(cut).Should().HaveCount(14);
    }

    [Fact]
    public async Task Con_quince_empresas_hay_buscador_que_filtra_sin_distinguir_tildes_ni_mayusculas()
    {
        _lista = [Propio(), .. Cartera(15)];
        var cut = Renderizar();
        await Abrir(cut);

        var buscador = cut.Find(".selector-tenant-busqueda");
        await buscador.InputAsync(new ChangeEventArgs { Value = "NERVION" });

        NombresDeLaLista(cut).Should().Equal("Nervión Instalaciones");
        cut.FindAll("[role=option]").Last().TextContent.Should().Contain("Tu organización",
            "el origen queda fijo bajo la lista aunque el filtro no lo alcance");

        await cut.Find(".selector-tenant-busqueda").InputAsync(new ChangeEventArgs { Value = "zzz" });
        NombresDeLaLista(cut).Should().BeEmpty();
        cut.Find(".selector-tenant-vacio").TextContent.Should().Contain("Ninguna empresa coincide");
    }

    [Fact]
    public async Task El_buscador_va_fuera_del_formulario_para_que_Intro_no_envie_un_POST_sin_Tenant()
    {
        _lista = [Propio(), .. Cartera(15)];
        var cut = Renderizar();
        await Abrir(cut);

        cut.FindAll(".selector-tenant-panel form .selector-tenant-busqueda").Should().BeEmpty();
        cut.FindAll(".selector-tenant-panel > .selector-tenant-busqueda").Should().ContainSingle();
    }

    [Fact]
    public async Task Los_nombres_largos_llevan_el_nombre_completo_en_title_y_en_aria_label()
    {
        const string largo = "Instalaciones Eléctricas y Mecánicas del Valle del Ebro y Levante Sociedad Limitada";
        _lista = [Propio(), Gestionado(Norte, largo), Gestionado(Sur, "Empresa Sur")];
        var cut = Renderizar(seleccionado: Norte);

        await Abrir(cut);

        var fila = cut.FindAll("[role=option]").First(o => o.GetAttribute("value") == Norte.ToString());
        fila.GetAttribute("title").Should().Be(largo);
        fila.GetAttribute("aria-label").Should().Be(largo);
        cut.Find(".selector-tenant-disparador .selector-tenant-nombre").GetAttribute("title").Should().Be(largo);
    }

    // --- Avatar y ventana de revalidación ------------------------------------------------------------

    [Fact]
    public void Sin_logo_el_avatar_pinta_las_iniciales_y_con_logo_la_imagen()
    {
        var sinLogo = Render<AvatarTenant>(p => p.Add(a => a.Nombre, "Empresa Norte"));
        sinLogo.Find(".avatar-tenant").TextContent.Trim().Should().Be("EN");
        sinLogo.FindAll("img").Should().BeEmpty();
        sinLogo.Find(".avatar-tenant").GetAttribute("aria-hidden").Should().Be("true");

        var conLogo = Render<AvatarTenant>(p => p
            .Add(a => a.Nombre, "Empresa Norte")
            .Add(a => a.LogoUrl, "/tenants/abc/logo?v=2"));
        conLogo.Find("img").GetAttribute("src").Should().Be("/tenants/abc/logo?v=2");
        conLogo.Find(".avatar-tenant").TextContent.Trim().Should().BeEmpty();
    }

    [Theory]
    [InlineData("Refrielectric Norte", "RN")]
    [InlineData("  ", "?")]
    [InlineData("& Co", "C")]
    [InlineData("Uno Dos Tres", "UD")]
    public void Las_iniciales_salen_de_las_dos_primeras_palabras_con_letra(string nombre, string esperadas) =>
        AvatarTenant.Iniciales(nombre).Should().Be(esperadas);

    [Fact]
    public void La_ventana_de_lectura_tras_caducar_una_cartera_queda_acotada_a_60_s_de_revalidacion_y_60_s_de_memoizacion()
    {
        // I3: el circuito revalida cada 60 s y la memoización del alcance caduca a los 60 s como
        // máximo. Subir cualquiera de las dos alarga la ventana en la que un Tenant caducado sigue
        // dando lecturas: este test obliga a decidirlo a propósito.
        RevalidacionCircuitoActivoHandler.IntervaloPorDefectoSegundos.Should().Be(60);
        CaducidadAlcanceOptions.CaducidadPorDefecto.Should().Be(TimeSpan.FromSeconds(60));
        CaducidadAlcanceOptions.MaximoSegundos.Should().Be(60);
    }
}
