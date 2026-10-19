using Bunit;
using System.Globalization;
using CaeManager.Domain.Centros;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class CentrosListaPatronTests
{
    [Theory]
    [InlineData("es-ES", "Centros", "+ Nuevo centro", "Agrupar: Por Cliente", "· 20 en esta página")]
    [InlineData("ca-ES", "Centres", "+ Nou centre", "Agrupa: Per Client", "· 20 en aquesta pàgina")]
    public void Los_rotulos_de_la_lista_salen_de_los_recursos_de_cada_idioma(
        string idioma, string titulo, string alta, string agrupar, string recuentoEnPagina)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(idioma);
            // 21 Centros del mismo Cliente: dos páginas, así que el título del grupo dice «en esta página».
            var cut = Renderizar(
                ConCentros(Enumerable.Range(1, 21).Select(i => CentroDe($"Centro {i:00}", ClienteOrion, EmpresaMontajes)).ToArray()),
                AgruparPorClienteUrl);
            cut.Find("h1").TextContent.Trim().Should().Be(titulo);
            cut.Find(".cabecera-listado-primaria button").TextContent.Trim().Should().Be(alta);
            DesplegableAgrupar(cut, idioma == "ca-ES" ? "Agrupa" : "Agrupar").TextContent.Trim().Should().Be(agrupar);
            cut.Find(".cabecera-grupo-centros-recuento").TextContent.Trim().Should().Be(recuentoEnPagina);
        }
        finally
        {
            CultureInfo.CurrentUICulture = anterior;
        }
    }

    [Fact]
    public void La_cabecera_ordena_seleccion_exportacion_teclado_y_menu_y_conserva_asignaciones()
    {
        var cut = Renderizar(ConDosClientes(), url: "centros?q=Planta");
        var acciones = cut.FindAll("header.cabecera-pagina .cabecera-listado-exportar, header.cabecera-pagina .cabecera-listado-icono, header.cabecera-pagina .menu-acciones-disparador");
        acciones.Select(a => a.GetAttribute("aria-label")).Should().Equal(
            "Selección múltiple", "Exportar a Excel", "Atajos de teclado", "Más acciones");
        acciones[1].GetAttribute("href").Should().StartWith("/centros/exportar.xlsx?").And.Contain("q=Planta");
        acciones[3].Click();
        cut.Find("a[href='/asignaciones/exportar.xlsx']").TextContent.Trim().Should().Be("Exportar asignaciones");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(21, false)]
    public void La_tarjeta_contiene_la_lista_y_el_pie_separado_permanece_incluso_sin_filas(int total, bool filtrada)
    {
        var cut = Renderizar(ConCentros(
            Enumerable.Range(1, total).Select(i => CentroDe($"Centro {i}", ClienteOrion, EmpresaMontajes, EstadoCentro.Vigente)).ToArray()),
            url: filtrada ? "centros?q=ausente" : "centros");
        cut.FindAll(".tarjeta-listado").Should().ContainSingle();
        cut.FindAll(".tarjeta-listado-centros").Should().BeEmpty("se reutiliza la tarjeta compartida");
        var tarjeta = cut.Find(".tarjeta-listado");
        if (total == 0)
        {
            tarjeta.QuerySelectorAll(".cabecera-columnas-centros, .lista-filas-acordeon").Should().BeEmpty("sin filas no hay cabecera de columnas");
            cut.FindAll(".tarjeta-listado-pie-separado .pie-lista-maqueta").Should().ContainSingle("el pie sigue debajo, con la presentación de la maqueta");
            cut.Find(".pie-lista-maqueta .paginador-texto").TextContent.Trim().Should().EndWith("Mostrando 0 de 0");
            cut.FindAll(".pie-lista-opciones select option").Select(b => b.TextContent.Trim()).Should().Equal("20", "50", "100");
            cut.Find(".pie-lista-atajos [data-pieza=leyenda-atajos]").Should().NotBeNull();
            return;
        }

        tarjeta.QuerySelectorAll(".tarjeta-listado-contenido .cabecera-columnas-centros").Should().ContainSingle();
        tarjeta.QuerySelectorAll(".tarjeta-listado-contenido .lista-filas-acordeon").Should().ContainSingle();
        tarjeta.QuerySelectorAll(".tarjeta-listado-contenido .cabecera-grupo-centros").Should().BeEmpty("de serie, sin agrupar");
        tarjeta.QuerySelectorAll(".tarjeta-listado-pie .paginador-pie-listado").Should().ContainSingle("con filas el pie va dentro de la tarjeta");
        cut.Find(".paginador-pie-listado .paginador-texto").TextContent.Trim().Should().EndWith($"Mostrando {Math.Min(20, total)} de {total}");
        cut.Find(".paginador-centro [data-pieza=leyenda-atajos]").Should().NotBeNull();
        cut.FindAll(".paginador-opciones-tamano button").Select(b => b.TextContent.Trim()).Should().Equal("20", "50", "100");
    }

    [Fact]
    public void Las_vistas_guardadas_tienen_pastilla_y_no_hay_Mas_filtros()
    {
        var cut = Renderizar(ConDosClientes());
        var pastillas = cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla");
        pastillas.Select(p => p.GetAttribute("aria-label")).Should().Contain("Filtros guardados").And.NotContain("Más filtros");
        Disparador(cut, "Filtros guardados").Click();
        cut.FindAll(".barra-filtros-pastillas [role=menuitem]").Should().Contain(
            item => item.TextContent.Trim() == "Guardar filtro" && item.HasAttribute("disabled"));
    }
}
