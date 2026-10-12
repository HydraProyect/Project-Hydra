using Bunit;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El icono de exportación de Proyectos lleva el Cliente empresarial del filtro, la búsqueda y el estado.
/// </summary>
public partial class ProyectosGen2Tests
{
    [Fact]
    public async Task Con_Cliente_empresarial_elegido_el_icono_exporta_la_vista_filtrada()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync("proyectos?estado=abiertos");
        cut.WaitForAssertion(() => cut.FindAll("tbody .nombre-proyecto").Should().ContainSingle());

        cut.FindAll("header.cabecera-pagina .menu-acciones").Should().ContainSingle();
        cut.Find("header.cabecera-pagina a[data-keytip=X]").GetAttribute("href")
            .Should().Be($"/proyectos/exportar.xlsx?cliente={ClienteId}&estado=abiertos");
    }

    [Fact]
    public void Sin_Cliente_empresarial_elegido_esta_vista_exporta_los_de_todos_sin_el_parametro_cliente()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];
        _mediator.ProyectosClienteB = [ProyectoDeB];
        var cut = Renderizar("proyectos?estado=abiertos");
        cut.WaitForAssertion(() => cut.FindAll("tbody .nombre-proyecto").Should().HaveCount(2));

        cut.Find("header.cabecera-pagina a[data-keytip=X]").GetAttribute("href")
            .Should().Be("/proyectos/exportar.xlsx?estado=abiertos");
    }

    /// <summary>El enlace no pagina: conserva los filtros y exporta la vista entera.</summary>
    [Fact]
    public async Task Exportar_esta_vista_no_limita_la_descarga_a_las_filas_de_la_pagina()
    {
        _mediator.Proyectos = Enumerable.Range(1, 23)
            .Select(i => ProyectoAbierto with { Id = Guid.NewGuid(), Nombre = $"Obra {i:00}" }).ToList();
        var cut = await RenderizarConClienteAsync();
        cut.FindAll("tbody .nombre-proyecto").Should().HaveCount(20, "control positivo: la página se queda en 20");

        cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("23");
        cut.Find("header.cabecera-pagina a[data-keytip=X]").GetAttribute("href")
            .Should().Be($"/proyectos/exportar.xlsx?cliente={ClienteId}");
    }
}
