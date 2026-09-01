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
[Collection("AppCollection")]
public class PestanaUrlDurableTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Cambiar_a_la_pestana_Revision_IA_de_Documentos_actualiza_la_URL_y_sobrevive_a_una_carga_en_frio()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos");

        await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Revisión IA" }).ClickAsync();
        await page.WaitForURLAsync($"{fixture.BaseUrl}/documentos?Pestana=revision-ia");

        // Carga en frío con esa URL exacta (no un clic dentro de la página ya
        // cargada): reproduce compartir el enlace o pulsar F5, no una
        // navegación "enhanced" que reutilizaría el circuito ya abierto.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos?Pestana=revision-ia");

        var pestanaActiva = page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Revisión IA" });
        await pestanaActiva.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        Assert.Equal("true", await pestanaActiva.GetAttributeAsync("aria-selected"));
    }

    [Fact]
    public async Task Cambiar_a_la_pestana_Generados_de_Plantillas_actualiza_la_URL_y_sobrevive_a_una_carga_en_frio()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/plantillas");

        await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { NameRegex = new System.Text.RegularExpressions.Regex("^Generados") }).ClickAsync();
        await page.WaitForURLAsync($"{fixture.BaseUrl}/plantillas?Pestana=generados");

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/plantillas?Pestana=generados");

        var pestanaActiva = page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { NameRegex = new System.Text.RegularExpressions.Regex("^Generados") });
        await pestanaActiva.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        Assert.Equal("true", await pestanaActiva.GetAttributeAsync("aria-selected"));
    }
}
