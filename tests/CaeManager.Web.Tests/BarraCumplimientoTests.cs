using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cumplimiento en una fila de listado (cierre de listados H, 2026-10-09): barra con la cifra escrita al
/// lado, en lugar del anillo de 24 px con cifra de 8,5 px. Mismos tramos de color que el anillo.
/// </summary>
public class BarraCumplimientoTests : BunitContext
{
    public BarraCumplimientoTests() => Services.AddLocalization();

    [Theory]
    [InlineData(100, "barra-cumplimiento-exito")]
    [InlineData(99, "barra-cumplimiento-advertencia")]
    [InlineData(50, "barra-cumplimiento-advertencia")]
    [InlineData(49, "barra-cumplimiento-peligro")]
    [InlineData(0, "barra-cumplimiento-peligro")]
    public void El_color_va_por_tramo_con_los_umbrales_del_anillo(int porcentaje, string claseEsperada)
    {
        var cut = Render<BarraCumplimiento>(p => p.Add(c => c.Porcentaje, porcentaje));

        var barra = cut.Find("[data-pieza=barra-cumplimiento]");
        barra.ClassList.Should().Contain(claseEsperada);
        barra.ClassList.Where(c => c.StartsWith("barra-cumplimiento-", StringComparison.Ordinal))
            .Should().ContainSingle("un solo tramo a la vez");
    }

    [Fact]
    public void La_cifra_va_escrita_al_lado_y_el_relleno_mide_el_porcentaje()
    {
        var cut = Render<BarraCumplimiento>(p => p.Add(c => c.Porcentaje, 67));

        cut.Find(".barra-cumplimiento-cifra").TextContent.Should().Be("67 %");
        cut.Find(".barra-cumplimiento-relleno").GetAttribute("style").Should().Be("width:67%");
    }

    [Fact]
    public void El_nombre_accesible_lleva_el_porcentaje_y_lo_visible_es_decorativo()
    {
        var cut = Render<BarraCumplimiento>(p => p.Add(c => c.Porcentaje, 67));

        var barra = cut.Find("[data-pieza=barra-cumplimiento]");
        barra.GetAttribute("role").Should().Be("img");
        barra.GetAttribute("aria-label").Should().Be("67 % de cumplimiento");
        cut.Find(".barra-cumplimiento-pista").GetAttribute("aria-hidden").Should().Be("true");
        cut.Find(".barra-cumplimiento-cifra").GetAttribute("aria-hidden").Should().Be("true",
            "el lector de pantalla no debe leer la cifra dos veces");
    }

    [Fact]
    public void La_etiqueta_propia_sustituye_al_nombre_accesible_por_defecto()
    {
        var cut = Render<BarraCumplimiento>(p => p
            .Add(c => c.Porcentaje, 72)
            .Add(c => c.Etiqueta, "72% de cumplimiento acumulado en 4 centros"));

        var barra = cut.Find("[data-pieza=barra-cumplimiento]");
        barra.GetAttribute("aria-label").Should().Be("72% de cumplimiento acumulado en 4 centros");
        barra.GetAttribute("title").Should().Be("72% de cumplimiento acumulado en 4 centros");
    }

    [Fact]
    public void Sin_universo_pinta_una_raya_y_no_anuncia_un_porcentaje()
    {
        var cut = Render<BarraCumplimiento>(p => p.Add(c => c.Etiqueta, "Sin actividad en ningún centro"));

        var barra = cut.Find("[data-pieza=barra-cumplimiento]");
        barra.ClassList.Should().Contain("barra-cumplimiento-sin-universo");
        barra.GetAttribute("aria-label").Should().Be("Sin actividad en ningún centro");
        barra.TextContent.Trim().Should().Be("—");
        cut.FindAll(".barra-cumplimiento-pista").Should().BeEmpty("sin universo no hay nada que medir: ni 0 % ni 100 %");
    }

    [Fact]
    public void Sin_universo_ni_etiqueta_dice_que_no_hay_requisitos()
    {
        var cut = Render<BarraCumplimiento>();

        cut.Find("[data-pieza=barra-cumplimiento]").GetAttribute("aria-label").Should().Be("Sin requisitos");
    }

    [Fact]
    public void Con_marca_la_barra_es_azul_sea_cual_sea_el_porcentaje()
    {
        foreach (var porcentaje in new[] { 10, 62, 100 })
        {
            var cut = Render<BarraCumplimiento>(p => p.Add(c => c.Porcentaje, porcentaje).Add(c => c.Marca, true));

            cut.Find("[data-pieza=barra-cumplimiento]").ClassList.Should().Contain("barra-cumplimiento-marca")
                .And.NotContain(["barra-cumplimiento-exito", "barra-cumplimiento-advertencia", "barra-cumplimiento-peligro"]);
        }
    }

    [Fact]
    public void Un_valor_fuera_de_rango_no_escribe_una_cifra_imposible()
    {
        var cut = Render<BarraCumplimiento>(p => p.Add(c => c.Porcentaje, 140));

        cut.Find(".barra-cumplimiento-cifra").TextContent.Should().Be("100 %");
    }

    [Fact]
    public void El_texto_propio_sustituye_a_la_cifra_y_un_valor_fuera_de_rango_no_desborda_la_pista()
    {
        var cut = Render<BarraCumplimiento>(p => p.Add(c => c.Porcentaje, 140).Add(c => c.Texto, "8/10"));

        cut.Find(".barra-cumplimiento-cifra").TextContent.Should().Be("8/10");
        cut.Find(".barra-cumplimiento-relleno").GetAttribute("style").Should().Be("width:100%");
    }
}
