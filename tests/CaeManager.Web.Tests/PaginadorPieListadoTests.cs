using System.Globalization;
using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public sealed class PaginadorPieListadoTests : BunitContext
{
    public PaginadorPieListadoTests() => Services.AddLocalization();

    [Theory]
    [InlineData("es-ES", 0, 0, "Mostrando 0 de 0")]
    [InlineData("es-ES", 1, 1, "Mostrando 1 de 1")]
    [InlineData("es-ES", 20, 55, "Mostrando 20 de 55")]
    [InlineData("es-ES", 5, 25, "Mostrando 5 de 25")]
    [InlineData("ca-ES", 0, 0, "Mostrant 0 de 0")]
    [InlineData("ca-ES", 5, 25, "Mostrant 5 de 25")]
    public void El_contador_usa_el_recuento_real_y_se_localiza(string cultura, int mostrados, int total, string esperado)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultura);
            var cut = Render<PaginadorSimple>(p => p
                .Add(c => c.ElementosEnPagina, mostrados).Add(c => c.TotalElementos, total)
                .Add(c => c.TotalPaginas, 1).Add(c => c.Centro, "<ul><li>Atajos</li></ul>"));

            cut.Find(".paginador-texto").TextContent.Should().Be(esperado);
            cut.Find(".paginador-centro ul").TextContent.Should().Be("Atajos");
            cut.FindAll(".paginador-opciones-tamano button").Select(b => b.TextContent).Should().Equal("20", "50", "100");
            cut.Find(".paginador-opciones-tamano").GetAttribute("aria-label").Should().Be(
                cultura == "ca-ES" ? "Files per pàgina:" : "Filas por página:");
            cut.FindAll("select").Should().BeEmpty();
        }
        finally
        {
            CultureInfo.CurrentUICulture = anterior;
        }
    }

    [Theory]
    [InlineData(1, true, false)]
    [InlineData(2, false, false)]
    [InlineData(3, false, true)]
    public async Task La_navegacion_respeta_los_limites_y_emite_la_pagina(int pagina, bool anteriorDeshabilitado, bool siguienteDeshabilitado)
    {
        var pedidas = new List<int>();
        var cut = Render<PaginadorSimple>(p => p.Add(c => c.ElementosEnPagina, 20)
            .Add(c => c.TotalPaginas, 3).Add(c => c.PaginaActual, pagina)
            .Add(c => c.PaginaActualChanged, (int nueva) => pedidas.Add(nueva)));
        var anterior = cut.Find("button[aria-label='Anterior']");
        var siguiente = cut.Find("button[aria-label='Siguiente']");

        anterior.HasAttribute("disabled").Should().Be(anteriorDeshabilitado);
        siguiente.HasAttribute("disabled").Should().Be(siguienteDeshabilitado);
        if (!siguienteDeshabilitado)
            await siguiente.ClickAsync(new MouseEventArgs());
        else
            await anterior.ClickAsync(new MouseEventArgs());
        pedidas.Should().Equal(siguienteDeshabilitado ? pagina - 1 : pagina + 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Una_pagina_no_oculta_el_contador_ni_los_tamanos(int totalPaginas)
    {
        var cut = Render<PaginadorSimple>(p => p.Add(c => c.ElementosEnPagina, 0).Add(c => c.TotalPaginas, totalPaginas));

        cut.FindAll(".paginador-inicio button").Should().BeEmpty();
        cut.FindAll(".paginador-opciones-tamano button").Should().HaveCount(3);
        cut.Find(".paginador-texto").TextContent.Should().Be("Mostrando 0 de 0");
    }

    [Fact]
    public async Task Pulsar_un_tamano_emite_el_valor_y_el_actual_se_marca()
    {
        var tamanos = new List<int>();
        var cut = Render<PaginadorSimple>(p => p.Add(c => c.ElementosEnPagina, 20)
            .Add(c => c.TamanoPaginaChanged, (int nuevo) => tamanos.Add(nuevo)));

        await cut.FindAll(".paginador-opciones-tamano button").Single(b => b.TextContent == "50").ClickAsync(new MouseEventArgs());
        cut.Render(p => p.Add(c => c.TamanoPagina, 50));

        tamanos.Should().Equal(50);
        cut.Find(".paginador-opciones-tamano button[aria-pressed='true']").TextContent.Should().Be("50");
    }

    [Fact]
    public void Los_consumidores_que_no_activan_el_pie_conservan_el_paginador_tradicional()
    {
        var cut = Render<PaginadorSimple>(p => p.Add(c => c.TotalElementos, 25).Add(c => c.TotalPaginas, 2)
            .Add(c => c.TamanoPaginaChanged, (int _) => { }));

        cut.Find(".paginador-texto").TextContent.Should().Contain("Página 1 de 2");
        cut.FindAll(".paginador-tamano-select").Should().ContainSingle();
        cut.FindAll(".paginador-pie-listado").Should().BeEmpty();
    }
}
