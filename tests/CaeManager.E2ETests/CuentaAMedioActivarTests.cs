using System.Net;
using Microsoft.Playwright;
using Xunit.Abstractions;

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
public class CuentaAMedioActivarTests(WebAppFixtureCuentaAMedioActivar fixture, ITestOutputHelper salida)
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
        await Assertions.Expect(AvisoDosFactoresObligatorio(pagina)).ToBeVisibleAsync();

        await AfirmarEstaticosServidosAsync(contexto, pagina);
    }

    /// <summary>
    /// El «bucle TOTP» de la demo (guion de Dirección, 2026-09-21): un
    /// Administrador sin 2FA que ya había cambiado la contraseña volvía una y
    /// otra vez a «Cambiar contraseña». El orden correcto es contraseña primero
    /// y 2FA después, sin volver atrás. Cambiar la contraseña tiene que reemitir
    /// el ticket con la obligación siguiente: si no, el middleware sigue leyendo
    /// «contraseña pendiente» en la cookie y devuelve a la cuenta a la pantalla
    /// que ya ha rellenado.
    /// </summary>
    [Fact]
    public async Task Administrador_con_contrasena_temporal_y_sin_2fa_cambia_la_contrasena_y_pasa_a_la_2fa_sin_volver_atras()
    {
        var email = Ayudas.EmailPrueba("administrador", 2);
        await fixture.EjecutarSqlAsync(
            "UPDATE \"AspNetUsers\" SET \"DebeCambiarContrasena\" = true, \"TwoFactorEnabled\" = false WHERE \"Email\" = @e",
            "e", email);

        await using var contexto = await fixture.Browser.NewContextAsync();
        var pagina = await contexto.NewPageAsync();
        var secuencia = RegistrarNavegaciones(pagina);

        await EntrarSinEsperarElMenuAsync(pagina, email);
        await pagina.GotoAsync($"{fixture.BaseUrl}/clientes");
        Assert.Contains("/cuenta/cambiar-contrasena", pagina.Url);

        await CambiarContrasenaAsync(pagina);
        Assert.True(pagina.Url.Contains("/cuenta/configurar-2fa", StringComparison.Ordinal),
            $"Tras cambiar la contraseña tiene que pedirse la 2FA. Secuencia: {string.Join(" → ", secuencia)}");
        await Assertions.Expect(AvisoDosFactoresObligatorio(pagina)).ToBeVisibleAsync();

        foreach (var ruta in new[] { "/clientes", "/" })
        {
            await pagina.GotoAsync($"{fixture.BaseUrl}{ruta}");
            Assert.True(pagina.Url.Contains("/cuenta/configurar-2fa", StringComparison.Ordinal),
                $"Tras cambiar la contraseña, {ruta} tiene que llevar a configurar la 2FA. Secuencia: {string.Join(" → ", secuencia)}");
        }

        salida.WriteLine("Secuencia de URLs: " + string.Join(" → ", secuencia));
        var tras = secuencia.SkipWhile(u => !u.Contains("/cuenta/configurar-2fa", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(tras, u => u.Contains("cambiar-contrasena", StringComparison.Ordinal));
    }

    /// <summary>
    /// Control de la otra mitad: una cuenta sin obligación de 2FA (Gestor CAE)
    /// que cambia su contraseña temporal entra directamente, y si abre la
    /// pantalla de 2FA por su cuenta no se le dice que su rol la exige.
    /// </summary>
    [Fact]
    public async Task Sin_obligacion_de_2fa_cambiar_la_contrasena_entra_y_la_pantalla_de_2fa_no_la_exige()
    {
        var email = Ayudas.EmailPrueba("gestorcae", 2);
        await fixture.EjecutarSqlAsync(
            "UPDATE \"AspNetUsers\" SET \"DebeCambiarContrasena\" = true WHERE \"Email\" = @e", "e", email);

        await using var contexto = await fixture.Browser.NewContextAsync();
        var pagina = await contexto.NewPageAsync();

        await EntrarSinEsperarElMenuAsync(pagina, email);
        Assert.Contains("/cuenta/cambiar-contrasena", pagina.Url);

        await CambiarContrasenaAsync(pagina);
        Assert.DoesNotContain("/cuenta/", pagina.Url);

        await pagina.GotoAsync($"{fixture.BaseUrl}/cuenta/configurar-2fa");
        await Assertions.Expect(pagina.GetByRole(AriaRole.Heading, new() { Name = "Autenticación en dos pasos" }))
            .ToBeVisibleAsync();
        await Assertions.Expect(AvisoDosFactoresObligatorio(pagina)).ToHaveCountAsync(0);
    }

    private static ILocator AvisoDosFactoresObligatorio(IPage pagina) =>
        pagina.GetByText("Tu rol de Administrador exige la autenticación en dos pasos");

    private static List<string> RegistrarNavegaciones(IPage pagina)
    {
        var secuencia = new List<string>();
        pagina.FrameNavigated += (_, marco) =>
        {
            if (marco == pagina.MainFrame)
                secuencia.Add(new Uri(marco.Url).PathAndQuery);
        };
        return secuencia;
    }

    /// <summary>
    /// Envía el formulario y espera a la navegación que produce, sea cual sea su
    /// destino: si el cambio no reemite el ticket, la cuenta vuelve a «Cambiar
    /// contraseña» y el test tiene que decirlo con la URL, no con un timeout.
    /// </summary>
    private static async Task CambiarContrasenaAsync(IPage pagina)
    {
        const string nueva = "Otra-Clave-2026-Segura";
        await pagina.GetByLabel("Contraseña actual", new() { Exact = true }).FillAsync(Ayudas.ContrasenaUsuariosPrueba);
        await pagina.GetByLabel("Contraseña nueva", new() { Exact = true }).FillAsync(nueva);
        await pagina.GetByLabel("Confirma la contraseña nueva", new() { Exact = true }).FillAsync(nueva);
        await pagina.RunAndWaitForNavigationAsync(
            () => pagina.GetByRole(AriaRole.Button, new() { Name = "Cambiar contraseña" }).ClickAsync());
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
