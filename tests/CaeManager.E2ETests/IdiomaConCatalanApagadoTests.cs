using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Catalán apagado (<c>Localizacion:CatalanHabilitado</c> = false, el valor por
/// defecto y el de la instancia de "AppCollection") sobre el transporte real:
/// una cookie <c>ca-ES</c> ya existente en el navegador no cambia nada —la
/// página sale en es-ES—, el selector no aparece ni en escritorio ni en móvil,
/// no hay «Català» visible en ninguna parte, y <c>POST /cuenta/idioma</c> con
/// <c>ca-ES</c> se rechaza sin tocar la cuenta.
///
/// <para>
/// Límite del instrumento: la cuenta con preferencia catalana guardada no se
/// siembra aquí (con el interruptor apagado no hay forma de guardarla por la
/// interfaz). Su efecto es exactamente una cookie <c>ca-ES</c> escrita en el
/// login —lo prueba <c>IdiomaPorCuentaTests</c> con el catalán encendido—, que
/// es lo que este test inyecta a mano.
/// </para>
/// </summary>
[Collection("AppCollection")]
public class IdiomaConCatalanApagadoTests(WebAppFixture fixture)
{
    private const string NombreCookie = ".AspNetCore.Culture";
    private const string ValorEspanol = "c%3Des-ES%7Cuic%3Des-ES";
    private const string ValorCatalan = "c%3Dca-ES%7Cuic%3Dca-ES";

    [Fact]
    public async Task Con_una_cookie_ca_ES_la_aplicacion_sale_en_es_ES_y_sin_selector_de_idioma()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        // Pantalla de acceso, antes de iniciar sesión: tampoco ahí hay idioma que elegir.
        await page.GotoAsync($"{fixture.BaseUrl}/cuenta/iniciar-sesion");
        Assert.DoesNotContain("Català", await page.ContentAsync());

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailGestorRefrielectric, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);

        // La cookie que dejaría una cuenta en catalán (o una sesión de cuando el
        // catalán estaba encendido): se conserva, pero no decide nada.
        await contexto.AddCookiesAsync(
        [
            new Cookie { Name = NombreCookie, Value = ValorCatalan, Url = fixture.BaseUrl },
        ]);

        Assert.Equal("es-ES", await CulturaServidaAsync(contexto));
        Assert.Contains(await contexto.CookiesAsync(), c => c.Name == NombreCookie && c.Value == ValorCatalan);

        await Ayudas.NavegarYEsperarAsync(page, fixture.BaseUrl);
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "es-ES");
        await Assertions.Expect(page.Locator(".menu-usuario")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("form.selector-idioma-formulario")).ToHaveCountAsync(0);
        Assert.DoesNotContain("Català", await page.ContentAsync());

        // Móvil: la cabecera cambia de forma, pero el selector sigue sin existir.
        await page.SetViewportSizeAsync(375, 812);
        await Ayudas.NavegarYEsperarAsync(page, fixture.BaseUrl);
        await Assertions.Expect(page.Locator("form.selector-idioma-formulario")).ToHaveCountAsync(0);
        Assert.DoesNotContain("Català", await page.ContentAsync());
    }

    [Fact]
    public async Task Cambiar_a_ca_ES_con_token_valido_se_rechaza_y_la_cuenta_sigue_en_es_ES()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailGestorRefrielectric, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);

        // El token antiforgery no va atado a un formulario: el de cerrar sesión
        // vale igual. Así el 400 es del interruptor, no de la falta de token
        // (ese caso ya lo cubre IdiomaPorCuentaTests).
        var token = await page.Locator("form[action='/cuenta/cerrar-sesion'] input[name='__RequestVerificationToken']")
            .GetAttributeAsync("value");
        Assert.False(string.IsNullOrEmpty(token), "sin token antiforgery el 400 no probaría el interruptor");

        var respuesta = await contexto.APIRequest.PostAsync($"{fixture.BaseUrl}/cuenta/idioma", new APIRequestContextOptions
        {
            Form = contexto.APIRequest.CreateFormData()
                .Append("idioma", "ca-ES").Append("returnUrl", "/").Append("__RequestVerificationToken", token!),
            MaxRedirects = 0,
        });

        Assert.Equal(400, respuesta.Status);
        Assert.DoesNotContain(SetCookie(respuesta), c => c.StartsWith($"{NombreCookie}="));

        // Control positivo del mismo camino: es-ES con el mismo token sí pasa.
        // Sin él, un 400 por cualquier otro motivo (token, formulario) pasaría
        // por rechazo del catalán.
        var respuestaEspanol = await contexto.APIRequest.PostAsync($"{fixture.BaseUrl}/cuenta/idioma", new APIRequestContextOptions
        {
            Form = contexto.APIRequest.CreateFormData()
                .Append("idioma", "es-ES").Append("returnUrl", "/").Append("__RequestVerificationToken", token!),
            MaxRedirects = 0,
        });
        Assert.Equal(302, respuestaEspanol.Status);
        Assert.Contains(SetCookie(respuestaEspanol), c => c.StartsWith($"{NombreCookie}={ValorEspanol};"));
    }

    // ── Ayudas ────────────────────────────────────────────────────────────

    /// <summary>Cultura con la que el servidor sirve la siguiente petición HTML, sin ejecutar JS de cliente.</summary>
    private async Task<string> CulturaServidaAsync(IBrowserContext contexto)
    {
        var respuesta = await contexto.APIRequest.GetAsync(fixture.BaseUrl);
        Assert.Equal(200, respuesta.Status);

        var contentLanguage = respuesta.Headers.GetValueOrDefault("content-language");
        var html = await respuesta.TextAsync();
        var lang = System.Text.RegularExpressions.Regex.Match(html, "<html lang=\"([^\"]*)\"").Groups[1].Value;

        Assert.Equal(lang, contentLanguage);
        return lang;
    }

    private static IReadOnlyList<string> SetCookie(IAPIResponse respuesta) =>
        respuesta.HeadersArray
            .Where(h => h.Name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
            .Select(h => h.Value)
            .ToList();
}
