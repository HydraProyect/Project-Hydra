using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Barra de herramientas de lista (Centro 360, PLAN-EJECUCION-UX.md § 0.9).
/// Lo que se cubre aquí es lo que las 9 listas dan por hecho al usarla: que
/// el botón de expandir solo existe si la lista es de acordeón, y que el
/// estado del toggle se comunica al lector de pantalla (no solo con color).
/// </summary>
public class BarraHerramientasListaTests : BunitContext
{
    [Fact]
    public void Sin_lista_de_acordeon_no_pinta_el_boton_de_expandir()
    {
        var cut = Render<BarraHerramientasLista>();

        cut.FindAll("button").Should().ContainSingle()
            .Which.TextContent.Should().Contain("Selección múltiple");
    }

    [Fact]
    public void Con_lista_de_acordeon_ofrece_expandir_o_colapsar_segun_el_estado()
    {
        var cut = Render<BarraHerramientasLista>(p => p
            .Add(b => b.TodosExpandidos, false)
            .Add(b => b.OnAlternarTodos, _ => { }));

        cut.Markup.Should().Contain("Expandir todos");

        cut.Render(p => p
            .Add(b => b.TodosExpandidos, true)
            .Add(b => b.OnAlternarTodos, _ => { }));

        cut.Markup.Should().Contain("Colapsar todos");
    }

    [Fact]
    public void El_estado_del_toggle_se_expone_con_aria_pressed()
    {
        var cut = Render<BarraHerramientasLista>(p => p.Add(b => b.SeleccionMultiple, true));

        cut.Find("button").GetAttribute("aria-pressed").Should().Be("true");
    }

    [Fact]
    public void Al_pulsar_el_toggle_notifica_el_valor_contrario()
    {
        bool? recibido = null;

        var cut = Render<BarraHerramientasLista>(p => p
            .Add(b => b.SeleccionMultiple, false)
            .Add(b => b.SeleccionMultipleChanged, valor => recibido = valor));

        cut.Find("button").Click();

        recibido.Should().BeTrue();
    }
}
