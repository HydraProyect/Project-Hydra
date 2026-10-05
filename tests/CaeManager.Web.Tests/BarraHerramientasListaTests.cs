using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Barra de herramientas de lista (Centro 360, Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.9).
/// Lo que se cubre aquí es lo que las listas dan por hecho al usarla: que
/// cada botón solo existe si la lista lo pide (selección múltiple solo con su
/// delegado; expandir solo si la lista es de acordeón), y que el estado del
/// toggle se comunica al lector de pantalla (no solo con color).
/// </summary>
public class BarraHerramientasListaTests : BunitContext
{
    public BarraHerramientasListaTests() => Services.AddLocalization();

    [Fact]
    public void Sin_lista_de_acordeon_no_pinta_el_boton_de_expandir()
    {
        var cut = Render<BarraHerramientasLista>(p => p.Add(b => b.SeleccionMultipleChanged, _ => { }));

        cut.FindAll("button").Should().ContainSingle()
            .Which.TextContent.Should().Contain("Selección múltiple");
    }

    /// <summary>
    /// Rediseño de listados, fase 1: las listas que llevan el ☑ en la cabecera no pasan el
    /// delegado y la barra no repite el conmutador. Control positivo: con delegado, sí lo pinta.
    /// </summary>
    [Fact]
    public void Sin_delegado_de_seleccion_no_pinta_el_conmutador_de_seleccion_multiple()
    {
        var sin = Render<BarraHerramientasLista>(p => p.Add(b => b.OnAlternarTodos, _ => { }));
        var con = Render<BarraHerramientasLista>(p => p
            .Add(b => b.OnAlternarTodos, _ => { })
            .Add(b => b.SeleccionMultipleChanged, _ => { }));

        sin.FindAll("button").Select(b => b.TextContent.Trim()).Should().Equal("Expandir todo");
        con.FindAll("button").Select(b => b.TextContent.Trim()).Should().Equal("Selección múltiple", "Expandir todo");
    }

    [Fact]
    public void Con_lista_de_acordeon_ofrece_expandir_o_contraer_todo_segun_el_estado()
    {
        var cut = Render<BarraHerramientasLista>(p => p
            .Add(b => b.TodosExpandidos, false)
            .Add(b => b.OnAlternarTodos, _ => { }));

        cut.Find("button").TextContent.Trim().Should().Be("Expandir todo");

        cut.Render(p => p
            .Add(b => b.TodosExpandidos, true)
            .Add(b => b.OnAlternarTodos, _ => { }));

        cut.Find("button").TextContent.Trim().Should().Be("Contraer todo");
    }

    [Fact]
    public void El_estado_del_toggle_se_expone_con_aria_pressed()
    {
        var cut = Render<BarraHerramientasLista>(p => p
            .Add(b => b.SeleccionMultiple, true)
            .Add(b => b.SeleccionMultipleChanged, _ => { }));

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
