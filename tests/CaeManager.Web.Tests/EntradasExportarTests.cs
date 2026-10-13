using System.Globalization;
using Bunit;
using CaeManager.Web.Exportacion;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>El menú ofrece solo la descarga completa; el icono ofrece la vista filtrada.</summary>
public class EntradasExportarTests : BunitContext
{
    public EntradasExportarTests()
    {
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Theory]
    [InlineData("es-ES", "Exportar todo")]
    [InlineData("ca-ES", "Exporta-ho tot")]
    public void Ofrece_exactamente_la_descarga_completa_localizada(string idioma, string etiqueta)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(idioma);
            var cut = Render<EntradasExportar>(p => p.Add(c => c.Ruta, "/trabajadores/exportar.xlsx"));
            var enlace = cut.FindAll("a[role=menuitem]").Should().ContainSingle().Which;
            enlace.TextContent.Trim().Should().Be(etiqueta);
            enlace.GetAttribute("href").Should().Be("/trabajadores/exportar.xlsx");
        }
        finally
        {
            CultureInfo.CurrentUICulture = anterior;
        }
    }
}
