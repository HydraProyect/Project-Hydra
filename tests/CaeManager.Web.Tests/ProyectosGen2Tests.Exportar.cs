using Bunit;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El «⋯» de la cabecera de Proyectos: las dos entradas de exportación (decisión D2 del
/// 2026-10-08). «Esta vista» lleva el Cliente empresarial del filtro, si hay uno, la búsqueda y el estado; «todo», nada.
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
    public void Sin_Cliente_empresarial_elegido_esta_vista_exporta_los_de_todos_sin_el_parametro_cliente()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];
        _mediator.ProyectosClienteB = [ProyectoDeB];
        var cut = Renderizar("proyectos?estado=abiertos");
        cut.WaitForAssertion(() => cut.FindAll("tbody .nombre-proyecto").Should().HaveCount(2));

        cut.Find("header.cabecera-pagina .menu-acciones-disparador").Click();

        cut.FindAll("header.cabecera-pagina .menu-acciones-item").Select(i => i.TextContent.Trim())
            .Should().Equal("Exportar esta vista (filas: 2)", "Exportar todo");
        cut.FindAll("header.cabecera-pagina a.menu-acciones-item").Select(i => i.GetAttribute("href"))
            .Should().Equal("/proyectos/exportar.xlsx?estado=abiertos", "/proyectos/exportar.xlsx");
    }

    /// <summary>«Filas» es el total de la vista, no las de la página que se ve: el fichero las lleva todas.</summary>
    [Fact]
    public async Task Exportar_esta_vista_cuenta_el_total_y_no_las_filas_de_la_pagina()
    {
        _mediator.Proyectos = Enumerable.Range(1, 23)
            .Select(i => ProyectoAbierto with { Id = Guid.NewGuid(), Nombre = $"Obra {i:00}" }).ToList();
        var cut = await RenderizarConClienteAsync();
        cut.FindAll("tbody .nombre-proyecto").Should().HaveCount(20, "control positivo: la página se queda en 20");

        cut.Find("header.cabecera-pagina .menu-acciones-disparador").Click();

        cut.FindAll("header.cabecera-pagina .menu-acciones-item").Select(i => i.TextContent.Trim())
            .Should().Contain("Exportar esta vista (filas: 23)");
    }
}
