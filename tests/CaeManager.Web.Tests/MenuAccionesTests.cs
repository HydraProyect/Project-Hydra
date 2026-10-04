using AngleSharp.Dom;
using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace CaeManager.Web.Tests;

public class MenuAccionesTests : BunitContext
{
    public MenuAccionesTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    [Fact]
    public async Task Abrir_por_clic_y_por_teclado_expone_el_menu_y_enfoca_el_item_esperado()
    {
        var cut = Renderizar();

        await Disparador(cut).ClickAsync(new MouseEventArgs());

        Disparador(cut).GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("[role=menu]").Id.Should().Be(Disparador(cut).GetAttribute("aria-controls"));
        cut.FindAll("[role=menuitem]").Should().OnlyContain(i => i.GetAttribute("tabindex") == "-1");
        FocoPedido(cut).Should().Be(ReferenciaItem(cut, "Primera").Id);

        await Disparador(cut).KeyDownAsync(new KeyboardEventArgs { Key = "ArrowUp" });

        Disparador(cut).GetAttribute("aria-expanded").Should().Be("true");
        FocoPedido(cut).Should().Be(ReferenciaItem(cut, "Última").Id);
    }

    [Fact]
    public async Task Flechas_inicio_y_fin_navegan_circularmente_y_omiten_el_item_deshabilitado()
    {
        var cut = Renderizar();
        await Disparador(cut).ClickAsync(new MouseEventArgs());

        cut.FindAll("[role=menuitem]").Single(i => i.TextContent.Trim() == "Sin acción").HasAttribute("disabled").Should().BeTrue();

        await Item(cut, "Primera").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });
        FocoPedido(cut).Should().Be(ReferenciaItem(cut, "Última").Id);

        await Item(cut, "Última").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });
        FocoPedido(cut).Should().Be(ReferenciaItem(cut, "Primera").Id);

        await Item(cut, "Primera").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowUp" });
        FocoPedido(cut).Should().Be(ReferenciaItem(cut, "Última").Id);

        await Item(cut, "Última").KeyDownAsync(new KeyboardEventArgs { Key = "Home" });
        FocoPedido(cut).Should().Be(ReferenciaItem(cut, "Primera").Id);

        await Item(cut, "Primera").KeyDownAsync(new KeyboardEventArgs { Key = "End" });
        FocoPedido(cut).Should().Be(ReferenciaItem(cut, "Última").Id);
    }

    [Fact]
    public async Task Escape_cierra_el_menu_y_devuelve_el_foco_al_disparador()
    {
        var cut = Renderizar();
        await Disparador(cut).ClickAsync(new MouseEventArgs());

        await Item(cut, "Primera").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        Disparador(cut).GetAttribute("aria-expanded").Should().Be("false");
        cut.FindAll("[role=menu]").Should().BeEmpty();
        FocoPedido(cut).Should().Be(ReferenciaDisparador(cut).Id);
    }

    // Sobre un <button>, Enter y Espacio disparan en el navegador el click nativo.
    // Si el keydown también ejecutara el ítem, la acción correría dos veces. bUnit
    // no emula ese click nativo: por eso se prueba que el keydown NO ejecuta y que
    // el click (lo que produce el navegador) ejecuta una sola vez y cierra.
    [Theory]
    [InlineData("Enter")]
    [InlineData(" ")]
    public async Task Enter_y_espacio_no_ejecutan_en_keydown_y_el_click_nativo_ejecuta_una_vez_y_cierra(string tecla)
    {
        var ejecuciones = 0;
        var cut = Renderizar(() => ejecuciones++);
        await Disparador(cut).ClickAsync(new MouseEventArgs());

        await Item(cut, "Primera").KeyDownAsync(new KeyboardEventArgs { Key = tecla });

        ejecuciones.Should().Be(0, "la activación por teclado la hace el click nativo del botón, no el keydown");
        Disparador(cut).GetAttribute("aria-expanded").Should().Be("true");

        await Item(cut, "Primera").ClickAsync(new MouseEventArgs());

        ejecuciones.Should().Be(1);
        Disparador(cut).GetAttribute("aria-expanded").Should().Be("false");
    }

    /// <summary>
    /// Un ítem con <c>Href</c> es un enlace de verdad (la exportación descarga por GET del
    /// servidor, no pasa por el circuito): <c>a[role=menuitem]</c> con su href, que cierra el
    /// menú al pulsarlo y entra en la navegación por flechas como cualquier otro.
    /// </summary>
    [Fact]
    public async Task Un_item_con_Href_es_un_enlace_del_menu_que_lo_cierra_al_pulsarlo()
    {
        var cut = Render<MenuAcciones>(p => p.AddChildContent((RenderFragment)(b =>
        {
            b.OpenComponent<ItemMenuAccion>(0);
            b.AddAttribute(1, nameof(ItemMenuAccion.Href), "/clientes/exportar.xlsx");
            b.AddAttribute(2, nameof(ItemMenuAccion.ChildContent), (RenderFragment)(c => c.AddContent(3, "Exportar a Excel")));
            b.CloseComponent();
            b.OpenComponent<ItemMenuAccion>(4);
            b.AddAttribute(5, nameof(ItemMenuAccion.OnClick), EventCallback.Factory.Create(this, () => { }));
            b.AddAttribute(6, nameof(ItemMenuAccion.ChildContent), (RenderFragment)(c => c.AddContent(7, "Otra")));
            b.CloseComponent();
        })));
        await Disparador(cut).ClickAsync(new MouseEventArgs());

        var enlace = cut.Find("a[role=menuitem]");
        enlace.GetAttribute("href").Should().Be("/clientes/exportar.xlsx");
        enlace.GetAttribute("tabindex").Should().Be("-1");

        await enlace.KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });
        FocoPedido(cut).Should().Be(ReferenciaItem(cut, "Otra").Id, "el enlace navega con flechas como los demás ítems");

        await cut.Find("a[role=menuitem]").ClickAsync(new MouseEventArgs());
        Disparador(cut).GetAttribute("aria-expanded").Should().Be("false");
    }

    /// <summary>
    /// Opciones de filtro (pastilla): <c>menuitemradio</c> con <c>aria-checked</c>, y al abrir el
    /// foco va a la marcada, no a la primera.
    /// </summary>
    [Fact]
    public async Task Las_opciones_marcables_son_menuitemradio_y_al_abrir_el_foco_va_a_la_marcada()
    {
        var cut = Render<MenuAcciones>(p => p
            .Add(m => m.Etiqueta, "Estado")
            .Add(m => m.Pastilla, true)
            .Add(m => m.Activa, true)
            .AddChildContent((RenderFragment)(b =>
            {
                b.OpenComponent<ItemMenuAccion>(0);
                b.AddAttribute(1, nameof(ItemMenuAccion.Marcado), (bool?)false);
                b.AddAttribute(2, nameof(ItemMenuAccion.OnClick), EventCallback.Factory.Create(this, () => { }));
                b.AddAttribute(3, nameof(ItemMenuAccion.ChildContent), (RenderFragment)(c => c.AddContent(4, "Todos")));
                b.CloseComponent();
                b.OpenComponent<ItemMenuAccion>(5);
                b.AddAttribute(6, nameof(ItemMenuAccion.Marcado), (bool?)true);
                b.AddAttribute(7, nameof(ItemMenuAccion.OnClick), EventCallback.Factory.Create(this, () => { }));
                b.AddAttribute(8, nameof(ItemMenuAccion.ChildContent), (RenderFragment)(c => c.AddContent(9, "Con vencidos")));
                b.CloseComponent();
            })));

        Disparador(cut).ClassList.Should().Contain(["menu-acciones-disparador-pastilla", "menu-acciones-disparador-activa"]);

        await Disparador(cut).ClickAsync(new MouseEventArgs());

        cut.FindAll("[role=menuitemradio]").Select(i => (i.TextContent.Trim(), i.GetAttribute("aria-checked")))
            .Should().Equal(("Todos", "false"), ("Con vencidos", "true"));
        cut.Find("[role=menu]").ClassList.Should().Contain("menu-acciones-panel-izquierda");
        FocoPedido(cut).Should().Be(ReferenciaItem(cut, "Con vencidos").Id);

        // Elegir una opción cierra y devuelve el foco a la pastilla: si no, se perdería con el panel.
        await cut.FindAll("[role=menuitemradio]").Single(i => i.TextContent.Trim() == "Todos").ClickAsync(new MouseEventArgs());

        cut.FindAll("[role=menu]").Should().BeEmpty();
        FocoPedido(cut).Should().Be(ReferenciaDisparador(cut).Id);
    }

    /// <summary>
    /// Una acción, en cambio, no devuelve el foco al disparador: suele abrir un drawer o un
    /// diálogo, que se queda con él.
    /// </summary>
    [Fact]
    public async Task Una_accion_cierra_el_menu_sin_devolver_el_foco_al_disparador()
    {
        var cut = Renderizar();
        await Disparador(cut).ClickAsync(new MouseEventArgs());

        await Item(cut, "Primera").ClickAsync(new MouseEventArgs());

        cut.FindAll("[role=menu]").Should().BeEmpty();
        FocoPedido(cut).Should().NotBe(ReferenciaDisparador(cut).Id);
    }

    /// <summary>
    /// Abierto sin ningún ítem habilitado el foco se queda en el disparador; Esc también cierra
    /// desde ahí (si no, el menú solo se cerraría con el ratón).
    /// </summary>
    [Fact]
    public async Task Escape_sobre_el_disparador_cierra_el_menu_abierto()
    {
        var cut = Renderizar();
        await Disparador(cut).ClickAsync(new MouseEventArgs());

        await Disparador(cut).KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        Disparador(cut).GetAttribute("aria-expanded").Should().Be("false");
        cut.FindAll("[role=menu]").Should().BeEmpty();
    }

    private IRenderedComponent<MenuAcciones> Renderizar(Action? primera = null)
    {
        var accionPrimera = primera ?? (() => { });
        return Render<MenuAcciones>(p => p.AddChildContent((RenderFragment)(b =>
        {
            b.OpenComponent<ItemMenuAccion>(0);
            b.AddAttribute(1, nameof(ItemMenuAccion.OnClick), EventCallback.Factory.Create(this, accionPrimera));
            b.AddAttribute(2, nameof(ItemMenuAccion.ChildContent), (RenderFragment)(c => c.AddContent(3, "Primera")));
            b.CloseComponent();
            b.OpenComponent<ItemMenuAccion>(4);
            b.AddAttribute(5, nameof(ItemMenuAccion.ChildContent), (RenderFragment)(c => c.AddContent(6, "Sin acción")));
            b.CloseComponent();
            b.OpenComponent<ItemMenuAccion>(7);
            b.AddAttribute(8, nameof(ItemMenuAccion.OnClick), EventCallback.Factory.Create(this, () => { }));
            b.AddAttribute(9, nameof(ItemMenuAccion.ChildContent), (RenderFragment)(c => c.AddContent(10, "Última")));
            b.CloseComponent();
        })));
    }

    private static IElement Disparador(IRenderedComponent<MenuAcciones> cut) => cut.Find(".menu-acciones-disparador");
    private static IElement Item(IRenderedComponent<MenuAcciones> cut, string texto) => cut.FindAll("[role=menuitem]").Single(i => i.TextContent.Trim() == texto);

    private static ElementReference ReferenciaItem(IRenderedComponent<MenuAcciones> cut, string texto)
    {
        var item = cut.FindComponents<ItemMenuAccion>().Single(c => c.Find("button, a").TextContent.Trim() == texto).Instance;
        var campo = typeof(ItemMenuAccion).GetField("_boton", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (ElementReference)campo.GetValue(item)!;
    }

    private static ElementReference ReferenciaDisparador(IRenderedComponent<MenuAcciones> cut)
    {
        var campo = typeof(MenuAcciones).GetField("_disparador", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (ElementReference)campo.GetValue(cut.Instance)!;
    }

    private string FocoPedido(IRenderedComponent<MenuAcciones> cut) => JSInterop.Invocations
        .Last(i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase))
        .Arguments[0].Should().BeOfType<ElementReference>().Which.Id!;
}
