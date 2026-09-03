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

        // El clic va por Ayudas.SeleccionarPestanaAsync y no a pelo: el botón
        // role="tab" lleva un @onclick server-side, así que un clic sobre el
        // prerenderizado estático se pierde en silencio y el fallo aparece 30 s
        // después aquí, en el WaitForURLAsync — medido en CI (REC-110, run
        // 33649091982 intento 1). El helper confirma por aria-selected que el
        // clic llegó al circuito antes de seguir.
        await Ayudas.SeleccionarPestanaAsync(
            page,
            page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Revisión IA" }),
            "Revisión IA");

        // Con el clic ya confirmado, un timeout aquí significa lo que este test
        // investiga —la pestaña activa no se reflejó en la URL— y no "el clic
        // se perdió", que es otra cosa y ya no puede confundirse con esto.
        //
        // Se mantienen los 30 s del default de Playwright, explícitos. Una
        // primera versión los bajó a 15 s aprovechando que el clic ya está
        // confirmado, y la revisión de Codex lo refutó con razón: confirmar
        // aria-selected NO confirma todavía la URL, porque el callback de la
        // página actualiza su estado primero y pide NavigateTo después (ver
        // Documentos.razor.cs). Una actualización de URL que bajo carga tardara
        // entre 15 y 30 s pasaba antes y habría empezado a fallar — cambiar un
        // intermitente por otro. El arreglo del clic perdido no necesita
        // estrechar este margen.
        await page.WaitForURLAsync(
            $"{fixture.BaseUrl}/documentos?Pestana=revision-ia",
            new PageWaitForURLOptions { Timeout = 30_000 });

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

        // Mismo motivo que en el test de arriba.
        await Ayudas.SeleccionarPestanaAsync(
            page,
            page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions
            {
                NameRegex = new System.Text.RegularExpressions.Regex("^Generados")
            }),
            "Generados");

        // 30 s explícitos, por el mismo motivo que en el test de arriba.
        await page.WaitForURLAsync(
            $"{fixture.BaseUrl}/plantillas?Pestana=generados",
            new PageWaitForURLOptions { Timeout = 30_000 });

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/plantillas?Pestana=generados");

        var pestanaActiva = page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { NameRegex = new System.Text.RegularExpressions.Regex("^Generados") });
        await pestanaActiva.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        Assert.Equal("true", await pestanaActiva.GetAttributeAsync("aria-selected"));
    }

    /// <summary>
    /// REC-062 (DEC-28, DDL-080): Plantillas se pliega en pestaña de
    /// Documentos conservando el mismo mecanismo de URL durable que las
    /// pestañas de arriba — mismo motivo, mismo helper.
    /// </summary>
    [Fact]
    public async Task Cambiar_a_la_pestana_Plantillas_de_Documentos_actualiza_la_URL_y_sobrevive_a_una_carga_en_frio()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos");

        // Mismo motivo que en los dos tests de arriba.
        await Ayudas.SeleccionarPestanaAsync(
            page,
            page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Plantillas" }),
            "Plantillas");

        // 30 s explícitos, por el mismo motivo que en los tests de arriba.
        await page.WaitForURLAsync(
            $"{fixture.BaseUrl}/documentos?Pestana=plantillas",
            new PageWaitForURLOptions { Timeout = 30_000 });

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos?Pestana=plantillas");

        var pestanaActiva = page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Plantillas" });
        await pestanaActiva.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        Assert.Equal("true", await pestanaActiva.GetAttributeAsync("aria-selected"));
    }
}
