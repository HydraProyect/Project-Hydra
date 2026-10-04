using AngleSharp.Dom;
using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pastilla de filtro de un listado (rediseño de listados, fase 1): un MenuAcciones con
/// disparador de pastilla, «Todos» y las opciones como menuitemradio. El teclado y el cierre
/// los hereda de MenuAcciones (ver MenuAccionesTests); aquí se prueba lo propio: el texto de la
/// pastilla, la opción marcada, «Todos» y que elegir lo mismo no avise.
/// </summary>
public class PastillaFiltroTests : BunitContext
{
    private static readonly IReadOnlyList<OpcionEstado> Opciones =
        [new("Vencido", "Con vencidos"), new("Urgente", "Con urgentes")];

    private readonly List<string> _elegidos = [];

    public PastillaFiltroTests()
    {
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<PastillaFiltro> Renderizar(string valor = "", bool ofrecerTodos = true) =>
        Render<PastillaFiltro>(p => p
            .Add(c => c.Etiqueta, "Estado")
            .Add(c => c.Opciones, Opciones)
            .Add(c => c.Valor, valor)
            .Add(c => c.OfrecerTodos, ofrecerTodos)
            .Add(c => c.ValorChanged, EventCallback.Factory.Create<string>(this, v => _elegidos.Add(v))));

    private static IElement Disparador(IRenderedComponent<PastillaFiltro> cut) => cut.Find(".menu-acciones-disparador");

    [Fact]
    public void Sin_valor_la_pastilla_dice_solo_el_nombre_del_filtro_y_no_esta_marcada()
    {
        var cut = Renderizar();

        var disparador = Disparador(cut);
        disparador.TextContent.Trim().Should().Be("Estado");
        disparador.GetAttribute("aria-label").Should().Be("Estado");
        disparador.GetAttribute("aria-haspopup").Should().Be("menu");
        disparador.GetAttribute("aria-expanded").Should().Be("false");
        disparador.ClassList.Should().Contain("menu-acciones-disparador-pastilla").And.NotContain("menu-acciones-disparador-activa");
    }

    [Fact]
    public void Con_valor_la_pastilla_dice_cual_y_se_marca()
    {
        var cut = Renderizar("Vencido");

        var disparador = Disparador(cut);
        disparador.TextContent.Trim().Should().Be("Estado: Con vencidos");
        disparador.ClassList.Should().Contain("menu-acciones-disparador-activa");
    }

    [Fact]
    public async Task Al_abrir_ofrece_Todos_y_las_opciones_con_la_vigente_marcada()
    {
        var cut = Renderizar("Urgente");

        await Disparador(cut).ClickAsync(new MouseEventArgs());

        Disparador(cut).GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll("[role=menu] [role=menuitemradio]").Select(i => (i.TextContent.Trim(), i.GetAttribute("aria-checked")))
            .Should().Equal(("Todos", "false"), ("Con vencidos", "false"), ("Con urgentes", "true"));
    }

    [Fact]
    public async Task Sin_valor_la_opcion_marcada_es_Todos()
    {
        var cut = Renderizar();

        await Disparador(cut).ClickAsync(new MouseEventArgs());

        cut.FindAll("[role=menuitemradio][aria-checked=true]").Select(i => i.TextContent.Trim()).Should().Equal("Todos");
    }

    [Fact]
    public async Task Elegir_una_opcion_avisa_con_su_valor_y_cierra_el_menu()
    {
        var cut = Renderizar();
        await Disparador(cut).ClickAsync(new MouseEventArgs());

        await cut.FindAll("[role=menuitemradio]").Single(i => i.TextContent.Trim() == "Con vencidos").ClickAsync(new MouseEventArgs());

        _elegidos.Should().Equal("Vencido");
        cut.FindAll("[role=menu]").Should().BeEmpty();
        var campo = typeof(MenuAcciones).GetField("_disparador", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var disparador = (ElementReference)campo.GetValue(cut.FindComponent<MenuAcciones>().Instance)!;
        JSInterop.Invocations.Last(i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase))
            .Arguments[0].Should().BeOfType<ElementReference>().Which.Id
            .Should().Be(disparador.Id, "el foco vuelve a la pastilla");
    }

    [Fact]
    public async Task Elegir_Todos_avisa_con_el_valor_vacio()
    {
        var cut = Renderizar("Vencido");
        await Disparador(cut).ClickAsync(new MouseEventArgs());

        await cut.FindAll("[role=menuitemradio]").Single(i => i.TextContent.Trim() == "Todos").ClickAsync(new MouseEventArgs());

        _elegidos.Should().Equal(string.Empty);
    }

    [Fact]
    public async Task Elegir_la_opcion_ya_vigente_no_avisa_para_no_recargar_la_lista()
    {
        var cut = Renderizar("Vencido");
        await Disparador(cut).ClickAsync(new MouseEventArgs());

        await cut.FindAll("[role=menuitemradio]").Single(i => i.TextContent.Trim() == "Con vencidos").ClickAsync(new MouseEventArgs());

        _elegidos.Should().BeEmpty();
        cut.FindAll("[role=menu]").Should().BeEmpty("el menú se cierra igual");
    }

    [Fact]
    public async Task Sin_OfrecerTodos_no_hay_opcion_para_quitar_el_filtro()
    {
        var cut = Renderizar(ofrecerTodos: false);

        await Disparador(cut).ClickAsync(new MouseEventArgs());

        cut.FindAll("[role=menuitemradio]").Select(i => i.TextContent.Trim()).Should().Equal("Con vencidos", "Con urgentes");
    }

    [Fact]
    public async Task Escape_cierra_la_pastilla_y_un_clic_fuera_tambien()
    {
        var cut = Renderizar();
        await Disparador(cut).ClickAsync(new MouseEventArgs());

        await cut.FindAll("[role=menuitemradio]")[0].KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });
        cut.FindAll("[role=menu]").Should().BeEmpty();

        await Disparador(cut).ClickAsync(new MouseEventArgs());
        await cut.Find(".menu-acciones-superposicion").ClickAsync(new MouseEventArgs());
        cut.FindAll("[role=menu]").Should().BeEmpty();
        Disparador(cut).GetAttribute("aria-expanded").Should().Be("false");
    }
}
