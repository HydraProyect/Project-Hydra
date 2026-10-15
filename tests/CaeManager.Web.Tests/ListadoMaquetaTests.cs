using System.Globalization;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public sealed class ListadoMaquetaTests : BunitContext
{
    public ListadoMaquetaTests()
    {
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Theory]
    [InlineData("es-ES", "Negocio", "Exportar a Excel")]
    [InlineData("ca-ES", "Negoci", "Exportar a Excel")]
    public void La_cabecera_rotula_el_grupo_y_la_exportacion_sin_duplicar_la_barra(string idioma, string grupo, string exportar)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(idioma);
            var cut = Render<CabeceraListado>(p => p.Add(c => c.Titulo, "Lista")
                .Add(c => c.ControlesEnBarra, true).Add(c => c.Grupo, "Negocio")
                .Add(c => c.Contador, 8).Add(c => c.SeleccionMultipleChanged, (bool _) => { })
                .Add(c => c.RutaExportar, "/trabajadores/exportar.xlsx"));

            cut.Find(".cabecera-pagina-kicker").TextContent.Trim().Should().Be(grupo);
            cut.Find("a.cabecera-listado-exportar").TextContent.Trim().Should().Be(exportar);
            cut.FindAll(".cabecera-listado-contador, button[aria-pressed]").Should().BeEmpty();
        }
        finally { CultureInfo.CurrentUICulture = anterior; }
    }

    [Theory]
    [InlineData("es-ES", "Buscar", "resultados")]
    [InlineData("ca-ES", "Cercar", "resultats")]
    public async Task La_barra_etiqueta_el_buscador_muestra_el_total_y_conmuta_la_seleccion(string idioma, string buscar, string resultados)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(idioma);
            var cambios = new List<bool>();
            var cut = Render<BarraFiltros>(p => p.Add(c => c.PresentacionMaqueta, true)
                .Add(c => c.Pastillas, (RenderFragment)(b => b.AddContent(0, "Filtro")))
                .Add(c => c.Contador, 8).Add(c => c.SeleccionMultipleChanged, (bool valor) => cambios.Add(valor)));

            var etiqueta = cut.Find(".barra-filtros-buscador-pantalla .campo label");
            etiqueta.TextContent.Should().Be(buscar);
            cut.Find("input[type=text]").Id.Should().Be(etiqueta.GetAttribute("for"));
            cut.Find(".lista-contador").TextContent.Should().Contain("8").And.Contain(resultados);
            var seleccion = cut.Find(".lista-herramientas-maqueta button[aria-pressed]");
            seleccion.GetAttribute("aria-pressed").Should().Be("false");
            await seleccion.ClickAsync(new MouseEventArgs());
            cambios.Should().Equal(true);
        }
        finally { CultureInfo.CurrentUICulture = anterior; }
    }

    [Theory]
    [InlineData("es-ES", "Página 1 de 1 — Mostrando 0 de 0", "Anterior", "Siguiente")]
    [InlineData("ca-ES", "Pàgina 1 de 1 — Mostrant 0 de 0", "Anterior", "Següent")]
    public void El_pie_vacio_mantiene_navegacion_deshabilitada_y_localiza_el_resumen(string idioma, string resumen, string anteriorTexto, string siguienteTexto)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(idioma);
            var cut = Render<PaginadorSimple>(p => p.Add(c => c.PresentacionMaqueta, true)
                .Add(c => c.ElementosEnPagina, 0).Add(c => c.TotalPaginas, 0));

            cut.Find(".paginador-texto").TextContent.Should().Be(resumen);
            cut.Find(".pie-lista-inicio button").TextContent.Trim().Should().Be(anteriorTexto);
            cut.Find(".pie-lista-opciones button").TextContent.Trim().Should().Be(siguienteTexto);
            cut.FindAll(".pie-lista-maqueta button").Should().HaveCount(2).And.OnlyContain(b => b.HasAttribute("disabled"));
            cut.Find("select").HasAttribute("disabled").Should().BeTrue();
        }
        finally { CultureInfo.CurrentUICulture = anterior; }
    }

    [Theory]
    [InlineData(1, true, false)]
    [InlineData(2, false, false)]
    [InlineData(3, false, true)]
    public async Task El_pie_conserva_los_limites_y_los_callbacks_de_pagina_y_tamano(int pagina, bool inicio, bool fin)
    {
        var paginas = new List<int>();
        var tamanos = new List<int>();
        var cut = Render<PaginadorSimple>(p => p.Add(c => c.PresentacionMaqueta, true)
            .Add(c => c.ElementosEnPagina, 20).Add(c => c.TotalPaginas, 3).Add(c => c.PaginaActual, pagina)
            .Add(c => c.PaginaActualChanged, (int valor) => paginas.Add(valor))
            .Add(c => c.TamanoPaginaChanged, (int valor) => tamanos.Add(valor)));

        var anterior = cut.Find(".pie-lista-inicio button");
        var siguiente = cut.Find(".pie-lista-opciones button");
        anterior.HasAttribute("disabled").Should().Be(inicio);
        siguiente.HasAttribute("disabled").Should().Be(fin);
        await (fin ? anterior : siguiente).ClickAsync(new MouseEventArgs());
        await cut.Find("select").ChangeAsync(new ChangeEventArgs { Value = "50" });
        paginas.Should().Equal(fin ? pagina - 1 : pagina + 1);
        tamanos.Should().Equal(50);
    }

    [Fact]
    public void El_pie_con_tamano_fijo_de_auditoria_muestra_su_valor_real()
    {
        var cut = Render<PaginadorSimple>(p => p.Add(c => c.PresentacionMaqueta, true)
            .Add(c => c.ElementosEnPagina, 30).Add(c => c.TotalElementos, 45)
            .Add(c => c.TotalPaginas, 2).Add(c => c.TamanoPagina, 30));

        var selector = cut.Find(".pie-lista-opciones select");
        selector.GetAttribute("value").Should().Be("30");
        selector.HasAttribute("disabled").Should().BeTrue();
        selector.QuerySelectorAll("option").Select(o => o.GetAttribute("value")).Should().Contain("30");
    }
}
