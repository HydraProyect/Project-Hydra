using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// El bug reportado (2026-09-18): con el sistema operativo en claro y la
/// cuenta en Oscuro, cada recarga mostraba unos segundos de claro antes de
/// saltar a oscuro. Causa: el HTML prerenderizado de <c>App.razor</c> nunca
/// llevaba <c>data-theme</c> — solo lo aplicaba <c>tema.js</c> tras conectar
/// el circuito (ver <c>SelectorTema.razor.cs</c>) — así que hasta entonces
/// ganaba el CSS por defecto (claro).
///
/// <para>
/// Estos tests leen el HTML **crudo** servido por el servidor, sin dejar que
/// se ejecute ningún JS de cliente — es la única forma de comprobar el
/// prerenderizado en sí, no su efecto tras la interoperación de JS (que ya
/// cubre <see cref="SelectorTemaTests"/>). <c>IBrowserContext.APIRequest</c>
/// comparte el cookie jar del contexto (mismo patrón que
/// <c>IncidenciasExportarAutorizacionE2ETests</c>) y no ejecuta el documento
/// que descarga, así que <c>data-theme</c> en el cuerpo de la respuesta solo
/// puede venir del servidor.
/// </para>
/// </summary>
[Collection("AppCollection")]
public class SelectorTemaPrerenderTests(WebAppFixture fixture)
{
    [Fact]
    public async Task El_HTML_prerenderizado_ya_lleva_data_theme_para_una_cuenta_con_tema_explicito()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(
            page, fixture.BaseUrl, Ayudas.EmailAdministradorConsultora, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);
        await Ayudas.NavegarYEsperarAsync(page, fixture.BaseUrl);

        // Elegir "oscuro" desde el circuito ya conectado: esto es lo que deja
        // la cookie de tema.js, y lo que este test comprueba que el servidor
        // usa en la SIGUIENTE petición, sin haber tocado JS para leerla.
        var selectorTema = page.Locator("select.selector-tema");
        await Assertions.Expect(selectorTema).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await selectorTema.SelectOptionAsync("oscuro");
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync(
            "data-theme", "oscuro", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });

        try
        {
            var respuesta = await contexto.APIRequest.GetAsync(fixture.BaseUrl);
            Assert.Equal(200, respuesta.Status);

            var html = await respuesta.TextAsync();
            Assert.Contains("<html lang=\"en\" data-theme=\"oscuro\">", html);
        }
        finally
        {
            // Devuelve la cuenta a su estado inicial: la fixture es
            // compartida por toda "AppCollection".
            await selectorTema.SelectOptionAsync("sistema");
            await Assertions.Expect(page.Locator("html")).Not.ToHaveAttributeAsync(
                "data-theme", "oscuro", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });
        }
    }

    /// <summary>
    /// "Sistema" nunca debe llevar data-theme (ver tokens.css: el
    /// auto-seguimiento de prefers-color-scheme está desactivado, así que un
    /// data-theme erróneo aquí no se corregiría solo por CSS).
    /// </summary>
    [Fact]
    public async Task El_HTML_prerenderizado_no_lleva_data_theme_cuando_la_cuenta_esta_en_Sistema()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(
            page, fixture.BaseUrl, Ayudas.EmailAdministradorConsultora, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);
        await Ayudas.NavegarYEsperarAsync(page, fixture.BaseUrl);

        var selectorTema = page.Locator("select.selector-tema");
        await Assertions.Expect(selectorTema).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        // Línea base explícita: fuerza "oscuro" y luego "sistema", para que
        // la ausencia de data-theme no sea casualidad de un cierre anterior.
        await selectorTema.SelectOptionAsync("oscuro");
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync(
            "data-theme", "oscuro", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });
        await selectorTema.SelectOptionAsync("sistema");
        await Assertions.Expect(page.Locator("html")).Not.ToHaveAttributeAsync(
            "data-theme", "oscuro", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });

        var respuesta = await contexto.APIRequest.GetAsync(fixture.BaseUrl);
        Assert.Equal(200, respuesta.Status);

        var html = await respuesta.TextAsync();
        Assert.DoesNotContain("data-theme", html);
    }

    /// <summary>
    /// Una página anónima (login) nunca lleva data-theme, aunque el
    /// navegador cargue una cookie de tema — <c>TemaCookie</c> exige
    /// <c>HttpContext.User.Identity.IsAuthenticated</c> antes de leerla.
    /// </summary>
    [Fact]
    public async Task El_login_no_lleva_data_theme_aunque_el_navegador_tenga_la_cookie_de_tema()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();

        await contexto.AddCookiesAsync(new[]
        {
            new Cookie
            {
                Name = "tema",
                Value = "oscuro",
                Url = fixture.BaseUrl,
            },
        });

        var respuesta = await contexto.APIRequest.GetAsync($"{fixture.BaseUrl}/cuenta/iniciar-sesion");
        Assert.Equal(200, respuesta.Status);

        var html = await respuesta.TextAsync();
        Assert.DoesNotContain("data-theme", html);
    }
}
