using System.Net;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Una cuenta a medio activar (contraseña temporal sin cambiar, o Administrador
/// sin 2FA) tiene que poder ver y rellenar la pantalla que la activa, y esa
/// pantalla tiene que ser la que le corresponde.
///
/// <para>
/// Medido en local el 2026-09-21: <c>CuentaAMedioActivarSinAccesoMiddleware</c>
/// contestaba 403 a los CSS/JS de <c>wwwroot</c> (no son navegación ni cuelgan
/// de <c>/cuenta/</c>), así que «Cambiar contraseña» salía sin estilos; y con
/// solo la 2FA pendiente cualquier navegación redirigía a «Cambiar
/// contraseña», que la cuenta no tiene que cambiar. Ejerce la aplicación
/// entera porque el middleware decide con el endpoint resuelto por el
/// enrutado, algo que un test de unidad solo puede simular.
/// </para>
/// </summary>
[Collection("AppCollectionCuentaAMedioActivar")]
public class CuentaAMedioActivarTests(WebAppFixtureCuentaAMedioActivar fixture)
{
    [Fact]
    public async Task Con_la_contrasena_temporal_la_pantalla_de_cambio_llega_con_sus_estaticos()
    {
        var email = Ayudas.EmailPrueba("gestorcae", 3);
        await fixture.EjecutarSqlAsync(
            "UPDATE \"AspNetUsers\" SET \"DebeCambiarContrasena\" = true WHERE \"Email\" = @e", "e", email);

        await using var contexto = await fixture.Browser.NewContextAsync();
        var pagina = await contexto.NewPageAsync();
        await EntrarSinEsperarElMenuAsync(pagina, email);

        // Cualquier navegación lleva a donde toca.
        await pagina.GotoAsync($"{fixture.BaseUrl}/clientes");
        Assert.Contains("/cuenta/cambiar-contrasena", pagina.Url);

        await AfirmarEstaticosServidosAsync(contexto, pagina);
    }

    [Fact]
    public async Task Con_solo_la_2fa_pendiente_se_va_a_configurarla_y_no_a_cambiar_la_contrasena()
    {
        var email = Ayudas.EmailPrueba("administrador", 3);
        await fixture.EjecutarSqlAsync(
            "UPDATE \"AspNetUsers\" SET \"TwoFactorEnabled\" = false WHERE \"Email\" = @e", "e", email);

        await using var contexto = await fixture.Browser.NewContextAsync();
        var pagina = await contexto.NewPageAsync();
        await EntrarSinEsperarElMenuAsync(pagina, email);

        await pagina.GotoAsync($"{fixture.BaseUrl}/clientes");
        Assert.Contains("/cuenta/configurar-2fa", pagina.Url);
        Assert.DoesNotContain("cambiar-contrasena", pagina.Url);

        await AfirmarEstaticosServidosAsync(contexto, pagina);
    }

    private async Task EntrarSinEsperarElMenuAsync(IPage pagina, string email)
    {
        await pagina.GotoAsync($"{fixture.BaseUrl}/cuenta/iniciar-sesion");
        await pagina.FillAsync("#email", email);
        await pagina.FillAsync("#password", Ayudas.ContrasenaUsuariosPrueba);
        await pagina.ClickAsync("button[type=\"submit\"]");
        await pagina.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>
    /// Pide con la cookie de la sesión cada hoja de estilos y cada script que la
    /// página referencia. Control positivo implícito: la lista no puede estar
    /// vacía, o el test no observaría nada.
    /// </summary>
    private async Task AfirmarEstaticosServidosAsync(IBrowserContext contexto, IPage pagina)
    {
        var rutas = await pagina.EvaluateAsync<string[]>(
            "() => [...document.querySelectorAll('link[rel=stylesheet][href], script[src]')]" +
            ".map(e => e.href || e.src).filter(u => u.startsWith(location.origin))");

        Assert.NotEmpty(rutas);
        Assert.Contains(rutas, r => r.EndsWith(".css", StringComparison.Ordinal));

        foreach (var ruta in rutas)
        {
            var respuesta = await contexto.APIRequest.GetAsync(ruta,
                new APIRequestContextOptions { MaxRedirects = 0 });
            Assert.True(respuesta.Status == (int)HttpStatusCode.OK,
                $"{ruta} devolvió {respuesta.Status}: una cuenta a medio activar tiene que recibir los estáticos de su propia pantalla.");
        }
    }
}
