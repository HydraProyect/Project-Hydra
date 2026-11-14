using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Hallazgo de revisión adversarial de Codex sobre el plegado de "Revisión IA"
/// (antes /documentos/revision-ia) y "Documentos generados" (antes
/// /plantillas/documentos-generados) en pestañas: al perder su ruta propia,
/// la sección elegida no sobrevivía a una recarga ni era compartible por URL
/// — ninguna prueba lo ejercitaba. Cubre justo esa propiedad: cambiar de
/// pestaña actualiza la URL (mismo mecanismo P1-18 que los filtros de la
/// rejilla) y una URL con esa pestaña la restaura al cargar en frío.
/// </summary>
[Collection("AppCollectionListados")]
public class PestanaUrlDurableTests(WebAppFixtureListados fixture)
{
    /// <summary>
    /// D-49 y PR8: Revisión IA es pantalla propia (/documentos/revision-ia) y /documentos ya no tiene barra de pestañas: los
    /// enlaces guardados con <c>?Pestana=revision-ia</c> (la URL durable que protegía este test) siguen sirviendo y redirigen a
    /// la pantalla propia en una carga en frío. La señal es la URL final y la pantalla pintada, sin pulsar ninguna pestaña.
    /// </summary>
    [Fact]
    public async Task El_enlace_antiguo_con_Pestana_revision_ia_redirige_en_frio_a_la_pantalla_propia()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        var rutaPropia = $"{fixture.BaseUrl}/documentos/revision-ia";

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

        // Carga en frío del enlace antiguo (compartido o guardado): reproduce pegarlo o pulsar F5, no una navegación
        // "enhanced" que reutilizaría el circuito ya abierto.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos?Pestana=revision-ia");

        await Assertions.Expect(page).ToHaveURLAsync(rutaPropia, new PageAssertionsToHaveURLOptions { Timeout = 30_000 });
        await Assertions.Expect(page.Locator("section.revision-ia")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
    }
    [Fact]
    public async Task Cambiar_a_la_pestana_Generados_de_Plantillas_actualiza_la_URL_y_sobrevive_a_una_carga_en_frio()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/plantillas");

        // Mismo motivo que en el test de arriba.
        // PR9: la vista se elige con el segmentado Catalogo | Generados (botones con aria-pressed), no con una barra role=tab.
        var vistaGenerados = page.Locator("button.segmentado-opcion", new PageLocatorOptions { HasTextRegex = new System.Text.RegularExpressions.Regex("^Generados") });
        await vistaGenerados.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        await vistaGenerados.ClickAsync(new LocatorClickOptions { Timeout = 15_000 });

        // 30 s explícitos, por el mismo motivo que en el test de arriba.
        await Assertions.Expect(page).ToHaveURLAsync(
            $"{fixture.BaseUrl}/plantillas?Pestana=generados",
            new PageAssertionsToHaveURLOptions { Timeout = 30_000 });

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/plantillas?Pestana=generados");

        var vistaActiva = page.Locator("button.segmentado-opcion", new PageLocatorOptions { HasTextRegex = new System.Text.RegularExpressions.Regex("^Generados") });
        await vistaActiva.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await Assertions.Expect(vistaActiva).ToHaveAttributeAsync("aria-pressed", "true", new LocatorAssertionsToHaveAttributeOptions { Timeout = 10_000 });
    }

    /// <summary>
    /// PR8 (P-5 y D-27): sin barra de pestañas, Plantillas se alcanza por su entrada del menú lateral y el enlace antiguo
    /// <c>/documentos?Pestana=plantillas</c> sigue sirviendo: redirige a <c>/plantillas</c>, también en una carga en frío.
    /// </summary>
    [Fact]
    public async Task El_enlace_antiguo_con_Pestana_plantillas_redirige_en_frio_a_Plantillas_y_el_menu_la_ofrece()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos?Pestana=plantillas");

        await Assertions.Expect(page).ToHaveURLAsync($"{fixture.BaseUrl}/plantillas", new PageAssertionsToHaveURLOptions { Timeout = 30_000 });

        // La pantalla de destino pinta su propio segmentado (Catálogo · Generados): señal de que cargó.
        await Assertions.Expect(page.Locator("button.segmentado-opcion", new PageLocatorOptions { HasTextRegex = new System.Text.RegularExpressions.Regex("^Generados") }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // Y el menú lateral lleva a ella sin pasar por /documentos.
        await Assertions.Expect(page.Locator("a.nav-item[href='plantillas']"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
    }
}