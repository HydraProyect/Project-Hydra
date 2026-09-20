using System.Net;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Instancia propia con el intervalo de revalidación de sesión a 2 s
/// (<c>Sesion:IntervaloRevalidacionSegundos</c>, 60 s por defecto): el corte
/// de una sesión tras desactivar la cuenta es «como mucho un intervalo», y
/// esperar 60 s por cada comprobación no cabe en una suite E2E. Colección
/// propia porque el test desactiva y reactiva cuentas de prueba, y el resto de
/// la suite no debe encontrárselas a medias.
/// </summary>
public sealed class WebAppFixtureConRevalidacionRapida : WebAppFixture
{
    protected override IReadOnlyDictionary<string, string> VariablesDeEntornoAdicionales() =>
        new Dictionary<string, string> { ["Sesion__IntervaloRevalidacionSegundos"] = "2" };
}

[CollectionDefinition("AppCollectionRevalidacionRapida")]
public class AppCollectionRevalidacionRapida : ICollectionFixture<WebAppFixtureConRevalidacionRapida>;

/// <summary>
/// «Desactivar» en /usuarios tiene que cortar las sesiones YA abiertas de esa
/// cuenta, no solo el inicio de sesión nuevo (auditoría de seguridad
/// 2026-09-20: antes, una cookie previa y un circuito de Blazor ya conectado
/// seguían leyendo, exportando y escribiendo indefinidamente).
///
/// <para>
/// Ejerce la aplicación entera —cookie real, circuito real, PostgreSQL real,
/// la pantalla de Usuarios real— porque la propiedad no la garantiza una sola
/// pieza: la rotación del stamp la hace la pantalla, el rechazo de la cookie el
/// <c>SecurityStampValidator</c> a través de <c>SignInManagerCuentaDesactivada</c>, y el
/// del circuito <c>ProveedorAutenticacionRevalidada</c>. Cada pieza tiene su
/// prueba en su capa (integración, arquitectura); esta demuestra que juntas
/// cortan.
/// </para>
///
/// <para>
/// Controles: la misma cookie funciona ANTES de desactivar; una segunda cuenta
/// activa sigue funcionando DESPUÉS (el corte no es genérico); y reactivar no
/// resucita la cookie antigua (eso solo lo garantiza la rotación del stamp,
/// no el rechazo por «cuenta desactivada»).
/// </para>
/// </summary>
[Collection("AppCollectionRevalidacionRapida")]
public class DesactivarCortaLaSesionTests(WebAppFixtureConRevalidacionRapida fixture)
{
    [Fact]
    public async Task Desactivar_una_cuenta_corta_su_cookie_y_su_circuito_y_reactivar_no_los_resucita()
    {
        var emailVictima = Ayudas.EmailPrueba("gestorcae", 1);
        var emailControl = Ayudas.EmailPrueba("gestorcae", 2);

        await using var contextoVictima = await fixture.Browser.NewContextAsync();
        var paginaVictima = await contextoVictima.NewPageAsync();
        await Ayudas.IniciarSesionAsync(paginaVictima, fixture.BaseUrl, emailVictima, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(paginaVictima, $"{fixture.BaseUrl}/clientes");

        await using var contextoControl = await fixture.Browser.NewContextAsync();
        var paginaControl = await contextoControl.NewPageAsync();
        await Ayudas.IniciarSesionAsync(paginaControl, fixture.BaseUrl, emailControl, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(paginaControl, $"{fixture.BaseUrl}/clientes");

        // La cookie de la víctima, guardada ANTES de desactivarla: es la que un
        // navegador abierto (o una exportación con curl) seguiría usando.
        var cookieVictima = await CabeceraCookieAsync(contextoVictima);
        var cookieControl = await CabeceraCookieAsync(contextoControl);

        // Control positivo: antes de desactivar, las dos sesiones funcionan.
        Assert.Equal(HttpStatusCode.OK, await EstadoDeClientesAsync(cookieVictima));
        Assert.Equal(HttpStatusCode.OK, await EstadoDeClientesAsync(cookieControl));

        await CambiarActivacionAsync(emailVictima, "Desactivar", "Usuario desactivado.");

        // Circuito: la página de la víctima, conectada por SignalR sin generar
        // ninguna petición, tiene que ser devuelta a la pantalla de acceso: el
        // proveedor de autenticación revalidado pasa el estado a anónimo y
        // SesionDelCircuito navega con forceLoad.
        await paginaVictima.WaitForURLAsync(
            url => url.Contains("/cuenta/iniciar-sesion"),
            new PageWaitForURLOptions { Timeout = 30_000 });

        // Cookie: la cookie de antes de desactivar ya no autentica. Sin
        // seguir redirecciones: el rechazo es el 302 hacia el acceso.
        Assert.Equal(HttpStatusCode.Redirect, await EstadoDeClientesAsync(cookieVictima));

        // Control negativo: el corte no es genérico, otra cuenta activa sigue.
        Assert.Equal(HttpStatusCode.OK, await EstadoDeClientesAsync(cookieControl));
        Assert.DoesNotContain("/cuenta/iniciar-sesion", paginaControl.Url);

        // Reactivar devuelve la posibilidad de entrar, pero NO resucita la
        // cookie antigua: solo la rotación del stamp al desactivar lo impide
        // (el rechazo por «cuenta desactivada» desaparece al reactivar).
        await CambiarActivacionAsync(emailVictima, "Reactivar", "Usuario reactivado.");
        Assert.Equal(HttpStatusCode.Redirect, await EstadoDeClientesAsync(cookieVictima));

        // Y la cuenta reactivada sí puede iniciar sesión de nuevo.
        await using var contextoNuevo = await fixture.Browser.NewContextAsync();
        var paginaNueva = await contextoNuevo.NewPageAsync();
        await Ayudas.IniciarSesionAsync(paginaNueva, fixture.BaseUrl, emailVictima, Ayudas.ContrasenaUsuariosPrueba);
        Assert.Equal(HttpStatusCode.OK, await EstadoDeClientesAsync(await CabeceraCookieAsync(contextoNuevo)));
    }

    private async Task CambiarActivacionAsync(string emailUsuario, string accion, string textoToast)
    {
        await using var contextoAdmin = await fixture.Browser.NewContextAsync();
        var paginaAdmin = await contextoAdmin.NewPageAsync();
        // Dirección CAE del mismo tenant que las cuentas de prueba: el
        // Administrador inicial (admin@caemanager.local) vive en otra
        // organización y no las ve en su lista de usuarios.
        await Ayudas.IniciarSesionAsync(
            paginaAdmin, fixture.BaseUrl, Ayudas.EmailPrueba("direccioncae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(paginaAdmin, $"{fixture.BaseUrl}/usuarios");

        var fila = paginaAdmin.Locator("tr").Filter(new LocatorFilterOptions { HasText = emailUsuario });
        await Ayudas.PulsarAccionDeMenuAsync(fila.Locator(".menu-acciones-disparador"), accion);

        await Assertions.Expect(paginaAdmin.Locator(".toast").Filter(new LocatorFilterOptions { HasText = textoToast }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
    }

    private static async Task<string> CabeceraCookieAsync(IBrowserContext contexto) =>
        string.Join("; ", (await contexto.CookiesAsync()).Select(c => $"{c.Name}={c.Value}"));

    /// <summary>
    /// GET /clientes con esa cookie y nada más (sin cookie jar, sin seguir
    /// redirecciones): 200 = sesión válida, 302 = rechazada hacia el acceso.
    /// </summary>
    private async Task<HttpStatusCode> EstadoDeClientesAsync(string cabeceraCookie)
    {
        using var manejador = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var cliente = new HttpClient(manejador) { BaseAddress = new Uri(fixture.BaseUrl) };
        using var peticion = new HttpRequestMessage(HttpMethod.Get, "/clientes");
        peticion.Headers.Add("Cookie", cabeceraCookie);
        using var respuesta = await cliente.SendAsync(peticion);
        return respuesta.StatusCode;
    }
}
