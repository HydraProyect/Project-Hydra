using Bunit;
using CaeManager.Web.Exportacion;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La pieza mantiene el modo de dos variantes y ofrece SoloTodo para los diez listados:
/// la vista filtrada se descarga desde el icono; el menú descarga sin criterios.
/// </summary>
public class EntradasExportarTests : BunitContext
{
    public EntradasExportarTests()
    {
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void SoloTodo_ofrece_exactamente_Exportar_todo_sin_los_criterios_de_la_vista()
    {
        var cut = Render<EntradasExportar>(p => p
            .Add(c => c.Ruta, "/trabajadores/exportar.xlsx")
            .Add(c => c.SoloTodo, true)
            .Add(c => c.Total, 37)
            .Add(c => c.Criterios, new Dictionary<string, string?> { ["q"] = "prieto", ["estado"] = "Vencido" }));

        var enlace = cut.FindAll("a[role=menuitem]").Should().ContainSingle().Which;
        enlace.TextContent.Trim().Should().Be("Exportar todo");
        enlace.GetAttribute("href").Should().Be("/trabajadores/exportar.xlsx");
    }

    [Fact]
    public void Son_dos_enlaces_esta_vista_con_su_recuento_y_todo()
    {
        var cut = Render<EntradasExportar>(p => p
            .Add(c => c.Ruta, "/trabajadores/exportar.xlsx")
            .Add(c => c.Total, 37)
            .Add(c => c.Criterios, new Dictionary<string, string?> { ["q"] = "prieto", ["estado"] = "Vencido" }));

        var enlaces = cut.FindAll("a[role=menuitem]");
        enlaces.Select(e => e.TextContent.Trim()).Should().Equal("Exportar esta vista (filas: 37)", "Exportar todo");
        enlaces[0].GetAttribute("href").Should().Be("/trabajadores/exportar.xlsx?q=prieto&estado=Vencido");
        enlaces[1].GetAttribute("href").Should().Be("/trabajadores/exportar.xlsx");
    }

    [Fact]
    public void Los_criterios_vacios_no_viajan_y_los_valores_se_codifican()
    {
        var cut = Render<EntradasExportar>(p => p
            .Add(c => c.Ruta, "/clientes/exportar.xlsx")
            .Add(c => c.Total, 2)
            .Add(c => c.Criterios, new Dictionary<string, string?>
            {
                ["q"] = "Peña & Hijos",
                ["estado"] = "",
                ["ejecutivo"] = null,
                ["desc"] = "true",
            }));

        cut.FindAll("a[role=menuitem]")[0].GetAttribute("href")
            .Should().Be("/clientes/exportar.xlsx?q=Pe%C3%B1a%20%26%20Hijos&desc=true");
    }

    [Fact]
    public void Sin_total_conocido_el_rotulo_no_dice_ningun_numero()
    {
        var cut = Render<EntradasExportar>(p => p.Add(c => c.Ruta, "/empresas/exportar.xlsx").Add(c => c.Total, (int?)null));

        cut.FindAll("a[role=menuitem]").Select(e => e.TextContent.Trim()).Should().Equal("Exportar esta vista", "Exportar todo");
    }

    [Fact]
    public void Sin_ningun_criterio_esta_vista_apunta_a_lo_mismo_que_todo()
    {
        var cut = Render<EntradasExportar>(p => p.Add(c => c.Ruta, "/empresas/exportar.xlsx").Add(c => c.Total, 5));

        cut.FindAll("a[role=menuitem]").Select(e => e.GetAttribute("href"))
            .Should().Equal("/empresas/exportar.xlsx", "/empresas/exportar.xlsx");
    }
}
