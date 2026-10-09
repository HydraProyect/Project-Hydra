using Bunit;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El «⋯» de la cabecera de Proyectos: las dos entradas de exportación (decisión D2 del
/// 2026-10-08). «Esta vista» lleva el Cliente empresarial del selector y los filtros; «todo», nada.
/// </summary>
public partial class ProyectosGen2Tests
{
    [Fact]
    public async Task Con_Cliente_empresarial_elegido_el_menu_de_cabecera_ofrece_exportar_esta_vista_y_exportar_todo()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync("proyectos?estado=abiertos");
        cut.WaitForAssertion(() => cut.FindAll("tbody .nombre-proyecto").Should().ContainSingle());

        cut.Find("header.cabecera-pagina .menu-acciones-disparador").Click();

        cut.FindAll("header.cabecera-pagina .menu-acciones-item").Select(i => i.TextContent.Trim())
            .Should().Equal("Exportar esta vista (filas: 1)", "Exportar todo");
        cut.FindAll("header.cabecera-pagina a.menu-acciones-item").Select(i => i.GetAttribute("href"))
            .Should().Equal($"/proyectos/exportar.xlsx?cliente={ClienteId}&estado=abiertos", "/proyectos/exportar.xlsx");
    }

    [Fact]
    public void Sin_Cliente_empresarial_elegido_la_cabecera_no_pinta_el_menu_de_exportar()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = Renderizar();

        cut.WaitForAssertion(() => cut.Find("header.cabecera-pagina h1").TextContent.Trim().Should().Be("Proyectos"));
        cut.FindAll("header.cabecera-pagina .menu-acciones-disparador").Should().BeEmpty("sin Cliente empresarial no hay vista que exportar");
    }
}
