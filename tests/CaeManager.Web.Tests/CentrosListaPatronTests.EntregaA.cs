using Bunit;
using System.Globalization;
using CaeManager.Domain.Centros;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class CentrosListaPatronTests
{
    [Theory]
    [InlineData("es-ES", "Centros", "+ Nuevo centro", "1 bloqueo cae")]
    [InlineData("ca-ES", "Centres", "+ Nou centre", "1 bloqueig cae")]
    public void Los_rotulos_de_la_entrega_A_salen_de_los_recursos_de_cada_idioma(
        string idioma, string titulo, string alta, string bloqueo)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(idioma);
            var cut = RenderizarConGruposContraidos(ConCentros(CentroDe("Centro Norte", ClienteOrion, EmpresaMontajes, EstadoCentro.Bloqueado)));
            cut.Find("h1").TextContent.Trim().Should().Be(titulo);
            cut.Find(".cabecera-listado-primaria button").TextContent.Trim().Should().Be(alta);
            cut.Find(".grupo-lista-resumen-estado").TextContent.Trim().Should().Be(bloqueo);
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
            "Exportar a Excel", "Atajos de teclado", "Más acciones");
        acciones[0].GetAttribute("href").Should().StartWith("/centros/exportar.xlsx?").And.Contain("q=Planta");
        acciones[2].Click();
        cut.Find("a[href='/asignaciones/exportar.xlsx']").TextContent.Trim().Should().Be("Exportar asignaciones");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(21, false)]
    public void La_tarjeta_contiene_la_tabla_y_el_pie_separado_permanece_incluso_sin_filas(int total, bool filtrada)
    {
        var cut = RenderizarConGruposContraidos(ConCentros(
            Enumerable.Range(1, total).Select(i => CentroDe($"Centro {i}", ClienteOrion, EmpresaMontajes, EstadoCentro.Vigente)).ToArray()),
            url: filtrada ? "centros?q=ausente" : "centros");
        cut.FindAll(".tarjeta-listado").Should().ContainSingle();
        cut.FindAll(".tarjeta-listado-centros").Should().BeEmpty("se reutiliza la tarjeta compartida");
        var tarjeta = cut.Find(".tarjeta-listado");
        tarjeta.QuerySelectorAll(".tarjeta-listado-contenido .cabecera-columnas-centros").Should().ContainSingle();
        tarjeta.QuerySelectorAll(".tarjeta-listado-contenido .lista-filas-acordeon").Should().ContainSingle();
        tarjeta.QuerySelectorAll(".tarjeta-listado-contenido .grupo-lista").Count().Should().Be(total > 0 ? 1 : 0);
        cut.FindAll(".tarjeta-listado-pie-separado .pie-lista-maqueta").Should().ContainSingle();
        cut.Find(".pie-lista-maqueta .paginador-texto").TextContent.Trim().Should().EndWith($"Mostrando {Math.Min(20, total)} de {total}");
        cut.Find(".pie-lista-atajos [data-pieza=leyenda-atajos]").Should().NotBeNull();
        cut.FindAll(".pie-lista-opciones select option").Select(b => b.TextContent.Trim()).Should().Equal("20", "50", "100");
    }

    [Theory]
    [InlineData(EstadoCentro.Bloqueado, "fila-tintada-peligro", "1 bloqueo cae")]
    [InlineData(EstadoCentro.Vencido, "fila-tintada-peligro", "1 vencido")]
    [InlineData(EstadoCentro.Proximo, "fila-tintada-aviso", "1 por vencer")]
    [InlineData(EstadoCentro.Urgente, "fila-tintada-aviso", "1 por vencer")]
    [InlineData(EstadoCentro.Vigente, null, null)]
    public void La_cabecera_de_grupo_destaca_recuento_y_resume_solo_estados_presentes(
        EstadoCentro estado, string? tinte, string? resumen)
    {
        var cut = RenderizarConGruposContraidos(ConCentros(CentroDe("Centro Norte", ClienteOrion, EmpresaMontajes, estado)));
        var grupo = cut.Find(".grupo-lista-destacado");
        if (tinte is not null) grupo.ClassList.Should().Contain(tinte);
        else grupo.ClassList.Should().NotContain("fila-tintada-peligro").And.NotContain("fila-tintada-aviso");
        grupo.QuerySelector(".grupo-lista-identidad .grupo-lista-contador")!.TextContent.Trim().Should().Be("1");
        var estados = grupo.QuerySelectorAll(".grupo-lista-resumen-estado");
        if (resumen is null) estados.Should().BeEmpty();
        else estados.Should().ContainSingle().Which.TextContent.Trim().Should().Be(resumen);
    }

    [Fact]
    public void Bloqueo_prevalece_sobre_por_vencer_y_el_resumen_plural_no_incluye_vigentes()
    {
        var cut = RenderizarConGruposContraidos(ConCentros(
            CentroDe("A", ClienteOrion, EmpresaMontajes, EstadoCentro.Bloqueado),
            CentroDe("B", ClienteOrion, EmpresaMontajes, EstadoCentro.Bloqueado),
            CentroDe("C", ClienteOrion, EmpresaMontajes, EstadoCentro.Proximo),
            CentroDe("D", ClienteOrion, EmpresaMontajes, EstadoCentro.Vigente)));
        var grupo = cut.Find(".grupo-lista-destacado");
        grupo.ClassList.Should().Contain("fila-tintada-peligro").And.NotContain("fila-tintada-aviso");
        grupo.QuerySelectorAll(".grupo-lista-resumen-estado").Select(e => e.TextContent.Trim())
            .Should().Equal("2 bloqueos cae", "1 por vencer");
        grupo.QuerySelector(".grupo-lista-contador")!.TextContent.Trim().Should().Be("4");
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
