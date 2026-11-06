using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Instancia propia con la revalidación de la cookie a 2 s, para que el
/// <c>SecurityStampValidator</c> —que lee la cuenta ANTES de que exista contexto de
/// Tenant— corra dentro del test. El tráfico como <c>cae_app_runtime</c> ya no es
/// propio de esta fixture: desde la mejora 1.7 del harness lo hereda de
/// <see cref="WebAppFixture"/>, como todas las demás.
/// </summary>
public sealed class WebAppFixtureBajoRuntime : WebAppFixture
{
    protected override IReadOnlyDictionary<string, string> VariablesDeEntornoAdicionales() =>
        new Dictionary<string, string>
        {
            ["Sesion__IntervaloRevalidacionSegundos"] = "2",
        };
}

[CollectionDefinition("AppCollectionBajoRuntime")]
public class AppCollectionBajoRuntime : ICollectionFixture<WebAppFixtureBajoRuntime>;

/// <summary>
/// Las entradas de cuenta que leen <c>AspNetUsers</c> ANTES de que exista
/// contexto de Tenant, con la RLS de esa tabla (P1-M1) efectiva: inicio de
/// sesión con y sin 2FA, intento fallido (escribe <c>AccessFailedCount</c>) y
/// revalidación de la cookie. La recuperación de contraseña no se cubre aquí:
/// responde igual exista o no la cuenta, así que no podría observar la RLS. Sin el ámbito de
/// identificación acotado del almacén de usuarios, la cuenta no sería visible
/// y ninguna de estas entradas funcionaría.
/// </summary>
[Collection("AppCollectionBajoRuntime")]
public class AccesoBajoRolRuntimeTests(WebAppFixtureBajoRuntime fixture)
{
    [Fact]
    public async Task El_Administrador_con_2FA_entra_y_la_cookie_se_revalida_bajo_cae_app_runtime()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var pagina = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(pagina, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

        // Control positivo: el tráfico de la web tiene que ir por cae_app_runtime, no por el propietario.
        Assert.True(await fixture.ConexionesComoRuntimeAsync() > 0, "Ninguna conexión de la web autentica como cae_app_runtime.");

        // Pasado el intervalo, la siguiente petición revalida el SecurityStamp:
        // lee la cuenta sin Tenant en el contexto.
        await Task.Delay(TimeSpan.FromSeconds(3));
        var respuesta = await pagina.GotoAsync($"{fixture.BaseUrl}/usuarios");
        Assert.True(respuesta!.Ok, $"/usuarios respondió {respuesta.Status}.");
        // Una cookie válida no puede rechazarse porque la RLS oculte la cuenta al revalidarla.
        Assert.DoesNotContain("/cuenta/iniciar-sesion", pagina.Url);
        // El nombre de la cuenta vive ahora en el panel del menú de usuario (avatar).
        await Ayudas.AbrirMenuDeUsuarioAsync(pagina);
        await Assertions.Expect(pagina.Locator("#menu-cuenta-panel").GetByRole(AriaRole.Menuitem, new LocatorGetByRoleOptions { Name = Ayudas.EmailAdministrador })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Un_Gestor_CAE_sin_2FA_entra_bajo_cae_app_runtime()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var pagina = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(
            pagina, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);

        await Assertions.Expect(pagina.Locator(".nav-principal")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Una_contrasena_incorrecta_se_rechaza_y_la_correcta_sigue_entrando()
    {
        var email = Ayudas.EmailPrueba("gestorcae", 2);
        await using var contexto = await fixture.Browser.NewContextAsync();
        var pagina = await contexto.NewPageAsync();

        await pagina.GotoAsync($"{fixture.BaseUrl}/cuenta/iniciar-sesion");
        await pagina.FillAsync("#email", email);
        await pagina.FillAsync("#password", "No-es-la-contrasena#1");
        await pagina.ClickAsync("button[type=\"submit\"]");

        // El intento fallido escribe AccessFailedCount en la cuenta: si esa
        // escritura se perdiera bajo RLS, la página no llegaría al aviso.
        await Assertions.Expect(pagina.Locator(".acceso-alerta[role=alert]")).ToBeVisibleAsync();

        await Ayudas.IniciarSesionAsync(pagina, fixture.BaseUrl, email, Ayudas.ContrasenaUsuariosPrueba);
        await Assertions.Expect(pagina.Locator(".nav-principal")).ToBeVisibleAsync();
    }
}
