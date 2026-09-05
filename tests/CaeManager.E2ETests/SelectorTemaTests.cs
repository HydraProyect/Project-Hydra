using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// <b>¿El tema elegido llega a verse?</b>
///
/// <para>
/// <c>SelectorTema</c> guarda la preferencia en la cuenta
/// (<c>ApplicationUser.Tema</c>) y la aplica sobre <c>&lt;html data-theme&gt;</c>
/// por interoperación de JS (<c>wwwroot/js/tema.js</c>). Son dos efectos
/// distintos y hasta ahora ninguna prueba cubría el segundo: el componente
/// guardaba la preferencia y no la aplicaba <b>nunca</b> —ni al cambiarla ni
/// al cargar la página—, porque la guarda de <c>OnAfterRenderAsync</c> no
/// podía dispararse (ver su doc-comment). El fallo era mudo: la cuenta
/// quedaba con el tema correcto y el usuario no veía ningún cambio.
/// </para>
///
/// <para>
/// Por eso este test comprueba las dos mitades por separado — se aplica <b>en
/// vivo</b> y sigue aplicado <b>tras recargar</b>. Solo la primera fallaría si
/// se rompiera la interoperación; solo la segunda, si se rompiera el guardado.
/// </para>
/// </summary>
[Collection("AppCollection")]
public class SelectorTemaTests(WebAppFixture fixture)
{
    [Fact]
    public async Task El_tema_elegido_se_aplica_al_documento_y_sobrevive_a_la_recarga()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(
            page, fixture.BaseUrl, Ayudas.EmailAdministradorConsultora, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);
        await Ayudas.NavegarYEsperarAsync(page, fixture.BaseUrl);

        // Línea base: sin preferencia explícita, "sistema" no pone el atributo
        // (ver tema.js) — así "aparece data-theme" significa algo.
        await Assertions.Expect(page.Locator("html")).Not.ToHaveAttributeAsync(
            "data-theme", "oscuro", new LocatorAssertionsToHaveAttributeOptions { Timeout = 10_000 });

        var selectorTema = page.Locator("select.selector-tema");
        await Assertions.Expect(selectorTema).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await selectorTema.SelectOptionAsync("oscuro");

        // (1) En vivo, por interoperación de JS sobre el documento actual.
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync(
            "data-theme", "oscuro", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });

        // (2) Tras una carga completa: la preferencia se guardó en la cuenta y
        // el componente vuelve a aplicarla en el circuito nuevo.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/empresas");
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync(
            "data-theme", "oscuro", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });

        // Devuelve la cuenta a su estado inicial: la fixture es compartida por
        // toda "AppCollection" y este usuario lo usan otros tests.
        await selectorTema.SelectOptionAsync("sistema");
        await Assertions.Expect(page.Locator("html")).Not.ToHaveAttributeAsync(
            "data-theme", "oscuro", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });
    }
}
