using System.Globalization;
using Bunit;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Exportacion;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class CabeceraListadoExportacionTests : BunitContext
{
    public CabeceraListadoExportacionTests()
    {
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void El_icono_descarga_la_vista_y_ocupa_su_sitio_entre_seleccion_y_teclado()
    {
        var cut = Render<CabeceraListado>(p => p
            .Add(c => c.Titulo, "Lista")
            .Add(c => c.RutaExportar, "/centros/exportar.xlsx")
            .Add(c => c.SeleccionMultipleChanged, EventCallback.Factory.Create<bool>(this, _ => { }))
            .Add(c => c.Menu, b => b.AddContent(0, "Asignaciones"))
            .Add(c => c.Primaria, b => b.AddContent(0, "Alta")));

        cut.FindAll(".acciones-cabecera [aria-label]").Select(e => e.GetAttribute("aria-label"))
            .Should().Equal("Selección múltiple", "Exportar a Excel", "Atajos de teclado", "Más acciones");
        var enlace = cut.Find("a[data-keytip=X]");
        enlace.HasAttribute("download").Should().BeTrue("la descarga no debe pasar por el router de Blazor");
        enlace.GetAttribute("href").Should().Be("/centros/exportar.xlsx");
        enlace.GetAttribute("title").Should().Be("Exportar a Excel");
    }

    [Theory]
    [InlineData("es-ES")]
    [InlineData("ca-ES")]
    public void El_nombre_accesible_de_exportacion_esta_localizado(string idioma)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(idioma);
            var cut = Render<CabeceraListado>(p => p.Add(c => c.Titulo, "Lista").Add(c => c.RutaExportar, "/visitas/exportar.xlsx"));
            cut.Find("a[data-keytip=X]").GetAttribute("aria-label").Should().Be("Exportar a Excel");
        }
        finally
        {
            CultureInfo.CurrentUICulture = anterior;
        }
    }

    [Fact]
    public void Icono_y_menu_exportan_los_mismos_criterios_codificados_y_omiten_los_vacios()
    {
        var criterios = new Dictionary<string, string?>
        {
            ["q"] = "Formación & EPI + revisión",
            ["estado"] = "Vencido,Proximo",
            ["empresa"] = null,
            ["cliente"] = " ",
            ["orden"] = "Nombre",
            ["desc"] = "true",
        };
        RenderFragment menu = b =>
        {
            b.OpenComponent<EntradasExportar>(0);
            b.AddAttribute(1, nameof(EntradasExportar.Ruta), "/documentos/exportar.xlsx");
            b.AddAttribute(2, nameof(EntradasExportar.Criterios), criterios);
            b.CloseComponent();
        };
        var cut = Render<CabeceraListado>(p => p.Add(c => c.Titulo, "Lista")
            .Add(c => c.RutaExportar, "/documentos/exportar.xlsx")
            .Add(c => c.CriteriosExportar, criterios).Add(c => c.Menu, menu));
        var enlace = cut.Find("a[data-keytip=X]").GetAttribute("href")!;
        cut.Find(".menu-acciones-disparador").Click();
        cut.FindAll("a.menu-acciones-item")[0].GetAttribute("href").Should().Be(enlace);
        var query = QueryHelpers.ParseQuery(new Uri("https://example.test" + enlace).Query);
        query["q"].ToString().Should().Be(criterios["q"]);
        query["estado"].ToString().Should().Be(criterios["estado"]);
        query.Keys.Should().BeEquivalentTo("q", "estado", "orden", "desc");
    }

    [Fact]
    public void Admite_la_ruta_ya_filtrada_de_Centros_A_sin_duplicar_el_enlace()
    {
        var cut = Render<CabeceraListado>(p => p.Add(c => c.Titulo, "Lista")
            .Add(c => c.RutaExportar, "/centros/exportar.xlsx?q=Planta")
            .Add(c => c.EtiquetaExportar, "Descargar la vista"));
        cut.FindAll("a.cabecera-listado-icono").Should().ContainSingle().Which.GetAttribute("href")
            .Should().Be("/centros/exportar.xlsx?q=Planta");
        cut.Find("a.cabecera-listado-icono").GetAttribute("aria-label").Should().Be("Descargar la vista");
    }

    [Fact]
    public void Sin_ruta_de_exportacion_no_hay_enlace_aunque_se_hayan_pasado_criterios()
    {
        var cut = Render<CabeceraListado>(p => p.Add(c => c.Titulo, "Lista")
            .Add(c => c.CriteriosExportar, new Dictionary<string, string?> { ["q"] = "Planta" }));
        cut.FindAll("a[data-keytip=X]").Should().BeEmpty();
        cut.FindAll(".menu-acciones").Should().BeEmpty();
        cut.Find("button[data-abrir-atajos]").Should().NotBeNull();
    }
}
