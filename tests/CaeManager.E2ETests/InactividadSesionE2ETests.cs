using Microsoft.Playwright;
using Xunit;

namespace CaeManager.E2ETests;

/// <summary>
/// La mitad de <c>CaducidadSesion</c> que solo existe en un navegador real: la cookie de
/// aplicación llega sin caducidad (termina al cerrar el navegador), <c>actividad-sesion.js</c>
/// envía la señal al hacer clic y el servidor la acepta con la sesión iniciada. Los plazos los
/// prueba <c>CaducidadSesionTests</c> con un reloj manual.
/// </summary>
[Collection("AppCollectionListados")]
public class InactividadSesionE2ETests(WebAppFixtureListados fixture)
{
    [Fact]
    public async Task La_cookie_no_es_persistente_y_un_clic_envia_la_senal_de_interaccion()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);

        var cookies = await contexto.CookiesAsync();
        Assert.Equal(-1, cookies.Single(c => c.Name == ".AspNetCore.Identity.Application").Expires);

        var senal = page.WaitForResponseAsync(r =>
            r.Request.Method == "POST" && r.Url.EndsWith("/cuenta/sesion/actividad", StringComparison.Ordinal));
        await page.Locator("body").ClickAsync(new LocatorClickOptions { Position = new Position { X = 1, Y = 1 } });

        Assert.Equal(204, (await senal).Status);
    }
}
