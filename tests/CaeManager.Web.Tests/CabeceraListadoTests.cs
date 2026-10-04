using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cabecera de listado de una línea (rediseño de listados, fase 1): título con su contador y,
/// a la derecha, ☑ «Selección múltiple», el menú «⋯» y la acción primaria. Sin antetítulo.
/// </summary>
public class CabeceraListadoTests : BunitContext
{
    public CabeceraListadoTests()
    {
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static RenderFragment Menu => b =>
    {
        b.OpenComponent<ItemMenuAccion>(0);
        b.AddAttribute(1, nameof(ItemMenuAccion.Href), "/clientes/exportar.xlsx");
        b.AddAttribute(2, nameof(ItemMenuAccion.ChildContent), (RenderFragment)(c => c.AddContent(3, "Exportar a Excel")));
        b.CloseComponent();
    };

    private static RenderFragment Primaria => b =>
    {
        b.OpenElement(0, "button");
        b.AddAttribute(1, "class", "primaria-de-prueba");
        b.AddContent(2, "+ Nuevo");
        b.CloseElement();
    };

    [Fact]
    public void Pinta_titulo_y_contador_en_una_linea_sin_antetitulo()
    {
        var cut = Render<CabeceraListado>(p => p.Add(c => c.Titulo, "Clientes empresariales").Add(c => c.Contador, 12));

        cut.Find("h1.titulo-pagina").TextContent.Trim().Should().Be("Clientes empresariales");
        cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("12");
        cut.FindAll(".cabecera-pagina-kicker").Should().BeEmpty();
    }

    [Fact]
    public void Sin_contador_no_pinta_el_numero_y_con_cero_si()
    {
        Render<CabeceraListado>(p => p.Add(c => c.Titulo, "Lista")).FindAll(".cabecera-listado-contador").Should().BeEmpty(
            "mientras carga no hay número que dar");
        Render<CabeceraListado>(p => p.Add(c => c.Titulo, "Lista").Add(c => c.Contador, 0))
            .Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("0");
    }

    [Fact]
    public void Las_acciones_van_en_orden_seleccion_menu_y_primaria()
    {
        var cut = Render<CabeceraListado>(p => p
            .Add(c => c.Titulo, "Lista")
            .Add(c => c.SeleccionMultipleChanged, EventCallback.Factory.Create<bool>(this, _ => { }))
            .Add(c => c.Menu, Menu)
            .Add(c => c.Primaria, Primaria));

        cut.Find(".acciones-cabecera").Children.Select(e => e.ClassList[0])
            .Should().Equal("cabecera-listado-icono", "menu-acciones", "primaria-de-prueba");
        var menu = cut.Find(".menu-acciones-disparador");
        menu.GetAttribute("aria-label").Should().Be("Más acciones");
        menu.GetAttribute("title").Should().Be("Más acciones", "el «⋯» no tiene texto: el title lo explica al pasar el ratón");
        menu.ClassList.Should().Contain("menu-acciones-disparador-borde");
    }

    [Fact]
    public async Task El_icono_de_seleccion_multiple_tiene_nombre_accesible_y_conmuta()
    {
        var recibidos = new List<bool>();
        var cut = Render<CabeceraListado>(p => p
            .Add(c => c.Titulo, "Lista")
            .Add(c => c.SeleccionMultiple, false)
            .Add(c => c.SeleccionMultipleChanged, EventCallback.Factory.Create<bool>(this, recibidos.Add)));

        var icono = cut.Find("button.cabecera-listado-icono");
        icono.GetAttribute("aria-label").Should().Be("Selección múltiple");
        icono.GetAttribute("aria-pressed").Should().Be("false");

        await icono.ClickAsync(new MouseEventArgs());
        recibidos.Should().Equal(true);

        cut.Render(p => p.Add(c => c.SeleccionMultiple, true));
        var activo = cut.Find("button.cabecera-listado-icono");
        activo.GetAttribute("aria-pressed").Should().Be("true");
        activo.ClassList.Should().Contain("cabecera-listado-icono-activo");
    }

    [Fact]
    public void Sin_delegado_ni_menu_no_pinta_ni_el_icono_ni_el_menu()
    {
        var cut = Render<CabeceraListado>(p => p.Add(c => c.Titulo, "Lista").Add(c => c.Primaria, Primaria));

        cut.FindAll(".cabecera-listado-icono").Should().BeEmpty();
        cut.FindAll(".menu-acciones").Should().BeEmpty();
        cut.Find(".primaria-de-prueba").TextContent.Should().Be("+ Nuevo");
    }

    [Fact]
    public async Task El_menu_abre_los_items_del_llamador()
    {
        var cut = Render<CabeceraListado>(p => p.Add(c => c.Titulo, "Lista").Add(c => c.Menu, Menu));

        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());

        cut.Find("[role=menu] a[role=menuitem]").GetAttribute("href").Should().Be("/clientes/exportar.xlsx");
    }
}
