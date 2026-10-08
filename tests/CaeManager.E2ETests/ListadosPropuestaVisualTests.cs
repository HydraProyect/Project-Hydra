using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

[Collection("AppCollection")]
public class ListadosPropuestaVisualTests(WebAppFixture fixture)
{
    [Theory]
    [InlineData("trabajadores", false)]
    [InlineData("empresas", false)]
    [InlineData("clientes", false)]
    [InlineData("centros", false)]
    [InlineData("subcontratas", false)]
    [InlineData("vehiculos", false)]
    [InlineData("documentos", false)]
    [InlineData("gestiones", false)]
    [InlineData("proyectos", false)]
    [InlineData("trabajadores", true)]
    [InlineData("empresas", true)]
    [InlineData("clientes", true)]
    [InlineData("centros", true)]
    [InlineData("subcontratas", true)]
    [InlineData("vehiculos", true)]
    [InlineData("documentos", true)]
    [InlineData("gestiones", true)]
    [InlineData("proyectos", true)]
    public async Task Cabecera_del_listado_respeta_la_geometria_de_la_propuesta(string ruta, bool oscuro)
    {
        await using var contexto = await fixture.Browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = 1440, Height = 1000 },
            ColorScheme = oscuro ? ColorScheme.Dark : ColorScheme.Light
        });
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl,
            Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/{ruta}");
        var titulo = page.Locator(".listado-propuesta > header h1.titulo-pagina");
        await Expect(titulo).ToBeVisibleAsync();
        await Expect(titulo).ToHaveCSSAsync("font-size", "22px");
        await Expect(titulo).ToHaveCSSAsync("font-weight", "700");
        await Expect(titulo).ToHaveCSSAsync("color", "rgb(22, 30, 39)");
        await Expect(page.Locator(".listado-propuesta")).ToHaveCSSAsync("background-color", "rgb(233, 238, 244)");
        var cabecera = page.Locator(".listado-propuesta > header");
        await Expect(cabecera).ToHaveCSSAsync("min-height", "48px");
        await Expect(cabecera).ToHaveCSSAsync("margin-bottom", "0px");
        var filtro = page.Locator(".listado-propuesta .barra-filtros-pastillas .campo-input");
        await Expect(filtro).ToHaveCountAsync(1);
        await Expect(filtro).ToHaveCSSAsync("font-size", "13px");
        await page.SetViewportSizeAsync(390, 844);
        await Expect(filtro).ToHaveCSSAsync("font-size", "16px");
        await page.SetViewportSizeAsync(1440, 1000);

        // Control de aislamiento: la cabecera general comparte componente y hoja
        // cargada, pero conserva el contrato visual de las pantallas de detalle.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/mi-firma");
        var tituloGeneral = page.Locator("header.cabecera-pagina h1.titulo-pagina");
        await Expect(tituloGeneral).ToBeVisibleAsync();
        await Expect(tituloGeneral).ToHaveCSSAsync("font-size", "36px");
        await Expect(page.Locator(".listado-propuesta")).ToHaveCountAsync(0);
    }
}
