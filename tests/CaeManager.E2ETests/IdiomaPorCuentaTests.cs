using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Idioma por cuenta (<c>ApplicationUser.Idioma</c>, es-ES por defecto, ca-ES
/// soportado) sobre el transporte real: los inicios de sesión emiten el
/// <c>Set-Cookie</c> de <c>.AspNetCore.Culture</c> desde la cuenta, el selector
/// la cambia solo tras persistir, cerrar sesión la borra, y la cultura de la
/// siguiente petición se ve en <c>&lt;html lang&gt;</c> y en
/// <c>Content-Language</c> — la única forma objetiva de verla mientras ca-ES
/// tenga el mismo texto que es-ES.
///
/// <para>
/// Límites del instrumento: el login con Microsoft no se ejercita aquí (no hay
/// proveedor OIDC de prueba); el cierre real del navegador tampoco —el estado
/// de Playwright conserva también las cookies de sesión—, así que la
/// persistencia se comprueba por la caducidad de la cookie.
/// </para>
/// </summary>
[Collection("AppCollection")]
public class IdiomaPorCuentaTests(WebAppFixture fixture)
{
    private const string NombreCookie = ".AspNetCore.Culture";
    private const string ValorEspanol = "c%3Des-ES%7Cuic%3Des-ES";
    private const string ValorCatalan = "c%3Dca-ES%7Cuic%3Dca-ES";

    [Fact]
    public async Task El_idioma_vive_en_la_cuenta_y_cada_inicio_de_sesion_reconstruye_la_cookie()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        // Login local sin 2FA (Gestor CAE de prueba): la cuenta está en es-ES y el POST de login lo
        // proyecta en la cookie.
        var setCookieLogin = await IniciarSesionLocalAsync(
            page, Ayudas.EmailGestorRefrielectric, Ayudas.ContrasenaUsuariosPrueba);
        Assert.Contains(setCookieLogin, c => c.StartsWith($"{NombreCookie}={ValorEspanol};"));
        await Ayudas.DescartarNotificacionesPendientesAsync(page);
        Assert.Equal("es-ES", await CulturaServidaAsync(contexto));

        try
        {
            // Selector → POST /cuenta/idioma → redirección → circuito nuevo en ca-ES.
            var respuestaCambio = await CambiarIdiomaAsync(page, "ca-ES");
            Assert.Contains(await SetCookieAsync(respuestaCambio), c => c.StartsWith($"{NombreCookie}={ValorCatalan};"));
            await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "ca-ES");
            Assert.Equal("ca-ES", await CulturaServidaAsync(contexto));

            // Persistente, no de sesión: caducidad a un año vista.
            var cookie = (await contexto.CookiesAsync()).Single(c => c.Name == NombreCookie);
            Assert.True(cookie.Expires > DateTimeOffset.UtcNow.AddDays(300).ToUnixTimeSeconds(),
                $"la cookie de cultura caduca en {cookie.Expires} (unix): una cookie de sesión se perdería al cerrar el navegador");
            Assert.Equal("/", cookie.Path);

            // Sin la cookie (otro navegador, cookies borradas) la sesión sigue
            // viva pero sale en es-ES: la cookie es solo una proyección…
            await contexto.ClearCookiesAsync(new BrowserContextClearCookiesOptions { Name = NombreCookie });
            Assert.Equal("es-ES", await CulturaServidaAsync(contexto));

            // …y el siguiente inicio de sesión la reconstruye desde la cuenta.
            var setCookieLogout = await CerrarSesionAsync(page);
            Assert.Contains(setCookieLogout, c => c.StartsWith($"{NombreCookie}=;") && c.Contains("path=/"));

            var setCookieRelogin = await IniciarSesionLocalAsync(
                page, Ayudas.EmailGestorRefrielectric, Ayudas.ContrasenaUsuariosPrueba);
            Assert.Contains(setCookieRelogin, c => c.StartsWith($"{NombreCookie}={ValorCatalan};"));
            await Ayudas.DescartarNotificacionesPendientesAsync(page);
            Assert.Equal("ca-ES", await CulturaServidaAsync(contexto));
        }
        finally
        {
            // La fixture es compartida por toda "AppCollection": la cuenta
            // vuelve a es-ES.
            await RestaurarEspanolAsync(page, Ayudas.EmailGestorRefrielectric);
        }
    }

    [Fact]
    public async Task Dos_usuarios_consecutivos_en_el_mismo_navegador_no_heredan_el_idioma()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await IniciarSesionLocalAsync(page, Ayudas.EmailGestorRefrielectric, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);

        try
        {
            await CambiarIdiomaAsync(page, "ca-ES");
            await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "ca-ES");

            await CerrarSesionAsync(page);
            Assert.DoesNotContain(await contexto.CookiesAsync(), c => c.Name == NombreCookie);

            // Usuario B (Administrador con 2FA), cuenta en es-ES: entra en es-ES aunque el anterior
            // estuviera en ca-ES.
            var setCookieB = await IniciarSesionLocalAsync(page, Ayudas.EmailAdministradorConsultora, Ayudas.ContrasenaUsuariosPrueba);
            Assert.Contains(setCookieB, c => c.StartsWith($"{NombreCookie}={ValorEspanol};"));
            await Ayudas.DescartarNotificacionesPendientesAsync(page);
            Assert.Equal("es-ES", await CulturaServidaAsync(contexto));

            await CerrarSesionAsync(page);
        }
        finally
        {
            await RestaurarEspanolAsync(page, Ayudas.EmailGestorRefrielectric);
        }
    }

    [Fact]
    public async Task El_login_con_2FA_proyecta_el_idioma_de_la_cuenta_en_la_cookie()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await page.GotoAsync($"{fixture.BaseUrl}/cuenta/iniciar-sesion");
        await page.FillAsync("#email", Ayudas.EmailAdministrador);
        await page.FillAsync("#password", Ayudas.ContrasenaAdministrador);
        var respuestaPassword = await page.RunAndWaitForResponseAsync(
            () => page.ClickAsync("button[type=\"submit\"]"), EsPostA("/cuenta/iniciar-sesion"));
        await page.WaitForURLAsync("**/cuenta/verificar-2fa**");

        // Contraseña correcta pero pendiente de 2FA: todavía no hay sesión, así
        // que tampoco cookie de cultura.
        Assert.DoesNotContain(await SetCookieAsync(respuestaPassword), c => c.StartsWith($"{NombreCookie}="));

        await page.FillAsync("#codigo", Ayudas.GenerarCodigoTotp(Ayudas.ClaveTotpAdministrador));
        var respuesta2fa = await page.RunAndWaitForResponseAsync(
            () => page.ClickAsync("button[type=\"submit\"]"), EsPostA("/cuenta/verificar-2fa"));

        Assert.Contains(await SetCookieAsync(respuesta2fa), c => c.StartsWith($"{NombreCookie}={ValorEspanol};"));
        await page.Locator(".nav-principal").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        Assert.Equal("es-ES", await CulturaServidaAsync(contexto));
    }

    [Fact]
    public async Task Cambiar_de_idioma_sin_token_antiforgery_se_rechaza_y_no_toca_nada()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailGestorRefrielectric, Ayudas.ContrasenaUsuariosPrueba);

        var respuesta = await contexto.APIRequest.PostAsync($"{fixture.BaseUrl}/cuenta/idioma", new APIRequestContextOptions
        {
            Form = contexto.APIRequest.CreateFormData().Append("idioma", "ca-ES").Append("returnUrl", "/"),
            MaxRedirects = 0,
        });

        Assert.Equal(400, respuesta.Status);
        Assert.DoesNotContain(SetCookie(respuesta), c => c.StartsWith($"{NombreCookie}="));
        Assert.Equal("es-ES", await CulturaServidaAsync(contexto));
    }

    // ── Ayudas ────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<string>> IniciarSesionLocalAsync(IPage page, string email, string password)
    {
        await page.GotoAsync($"{fixture.BaseUrl}/cuenta/iniciar-sesion");
        await page.FillAsync("#email", email);
        await page.FillAsync("#password", password);
        var respuesta = await page.RunAndWaitForResponseAsync(
            () => page.ClickAsync("button[type=\"submit\"]"), EsPostA("/cuenta/iniciar-sesion"));

        // Cuentas con 2FA (los Administradores de prueba): la sesión —y con
        // ella la cookie de cultura— la abre el POST del código, no el de la
        // contraseña.
        if (respuesta.Headers.GetValueOrDefault("location")?.Contains("/cuenta/verificar-2fa") == true)
        {
            await page.WaitForURLAsync("**/cuenta/verificar-2fa**");
            await page.FillAsync("#codigo", Ayudas.GenerarCodigoTotp(Ayudas.ClaveTotpAdministrador));
            respuesta = await page.RunAndWaitForResponseAsync(
                () => page.ClickAsync("button[type=\"submit\"]"), EsPostA("/cuenta/verificar-2fa"));
        }

        try
        {
            await page.Locator(".nav-principal").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        catch (TimeoutException)
        {
            var cuerpo = await page.Locator("body").InnerTextAsync();
            throw new Xunit.Sdk.XunitException(
                $"El login de {email} no llegó a la aplicación: POST {respuesta.Status}, " +
                $"location «{respuesta.Headers.GetValueOrDefault("location")}», URL final {page.Url}. " +
                $"Página: {cuerpo[..Math.Min(cuerpo.Length, 400)]}");
        }

        return await SetCookieAsync(respuesta);
    }

    private static async Task<IResponse> CambiarIdiomaAsync(IPage page, string cultura)
    {
        var respuesta = await page.RunAndWaitForResponseAsync(
            () => page.SelectOptionAsync("select.selector-idioma", cultura),
            EsPostA("/cuenta/idioma"));

        Assert.True(respuesta.Status is >= 300 and < 400,
            $"POST a /cuenta/idioma devolvió {respuesta.Status}: el idioma no se guardó en la cuenta");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        return respuesta;
    }

    private static async Task<IReadOnlyList<string>> CerrarSesionAsync(IPage page)
    {
        var respuesta = await page.RunAndWaitForResponseAsync(
            () => page.ClickAsync(".boton-cerrar-sesion"), EsPostA("/cuenta/cerrar-sesion"));
        await page.WaitForURLAsync("**/cuenta/iniciar-sesion**");
        return await SetCookieAsync(respuesta);
    }

    /// <summary>
    /// Sesión nueva de la cuenta (el login reconstruye la cookie desde ella, así
    /// que la página refleja lo que la cuenta tiene guardado, no lo que quedara
    /// en este navegador) y, si está en ca-ES, vuelta a es-ES.
    /// </summary>
    private async Task RestaurarEspanolAsync(IPage page, string email)
    {
        await Ayudas.NavegarYEsperarAsync(page, fixture.BaseUrl);
        if (await page.Locator(".boton-cerrar-sesion").IsVisibleAsync())
            await CerrarSesionAsync(page);

        await IniciarSesionLocalAsync(page, email, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);

        if (await page.Locator("html").GetAttributeAsync("lang") is "ca-ES")
            await CambiarIdiomaAsync(page, "es-ES");
    }

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

    private static async Task<IReadOnlyList<string>> SetCookieAsync(IResponse respuesta) =>
        (await respuesta.HeadersArrayAsync())
            .Where(h => h.Name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
            .Select(h => h.Value)
            .ToList();

    private static IReadOnlyList<string> SetCookie(IAPIResponse respuesta) =>
        respuesta.HeadersArray
            .Where(h => h.Name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
            .Select(h => h.Value)
            .ToList();

    private static Func<IResponse, bool> EsPostA(string ruta) =>
        r => r.Request.Method == "POST" && new Uri(r.Url).AbsolutePath == ruta;
}
