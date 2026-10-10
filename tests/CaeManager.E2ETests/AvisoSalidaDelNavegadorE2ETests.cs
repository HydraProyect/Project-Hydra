using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// «Atrás» del navegador y el clic en un enlace del menú, con una edición a medias, preguntan «¿Salir sin guardar?».
/// Ninguna de las dos navegaciones pasa por el <c>NavigationLock</c> de <c>AvisoCambiosSinGuardar</c>: el Router no es
/// interactivo y las atiende la navegación mejorada en el cliente. Las lleva hasta el aviso
/// <c>wwwroot/js/aviso-salida-navegador.js</c>, y esa mitad solo existe en un navegador real: en bUnit el
/// NavigationManager de prueba sí llama al NavigationLock, y por eso el defecto pasó sin verse.
///
/// <para>
/// Dos pantallas de forma distinta: Trabajadores (lista QuickGrid, el formulario vive en la ficha del Context
/// Workspace, que está en el layout y se cierra en cuanto el circuito sabe que la URL cambió) y Centros (lista en
/// acordeón, el formulario es un drawer de la propia página). En las dos: «Seguir editando» conserva lo escrito y
/// la URL de la vista, y «Salir y descartar» repite la navegación detenida.
/// </para>
/// </summary>
[Collection("AppCollectionListados")]
public class AvisoSalidaDelNavegadorE2ETests(WebAppFixtureListados fixture)
{
    private static ILocator EnlaceDelMenu(IPage page, string ruta) =>
        page.Locator($"a.nav-item[href='{ruta}']");

    private static ILocator Pregunta(IPage page) =>
        page.GetByRole(AriaRole.Dialog).Filter(new LocatorFilterOptions
        { Has = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Seguir editando", Exact = true }) });

    private static async Task ResponderAsync(IPage page, string respuesta)
    {
        var boton = Pregunta(page).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = respuesta, Exact = true });
        await boton.FocusAsync();
        await boton.PressAsync("Space");
        await Expect(Pregunta(page)).ToBeHiddenAsync();
    }

    private static Task AtrasAsync(IPage page) => page.EvaluateAsync("() => history.back()");

    private static Regex UrlExacta(string url) => new("^" + Regex.Escape(url) + "$");

    private async Task<IPage> EntrarAsync(IBrowserContext contexto, string primeraPantalla)
    {
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 800);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        // Primera entrada del historial: una carga de documento. La segunda llega por el menú, sin recargar.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/{primeraPantalla}");
        return page;
    }

    [Fact]
    public async Task Trabajadores_con_la_ficha_a_medias_atras_y_el_enlace_del_menu_preguntan_y_seguir_editando_conserva_la_edicion_y_la_url()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await EntrarAsync(contexto, "centros");
        await EnlaceDelMenu(page, "trabajadores").ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex(@"/trabajadores$"));
        await Expect(page).ToHaveTitleAsync(new Regex("trabajadores", RegexOptions.IgnoreCase));
        // La siembra no tiene un trabajador de nombre estable: vale cualquiera cuyo rótulo accesible no se repita.
        var nombres = page.Locator("tbody tr.fila-pulsable button.nombre-abre-vista-rapida");
        await Expect(nombres).Not.ToHaveCountAsync(0);
        var rotulos = await nombres.EvaluateAllAsync<string[]>("botones => botones.map(b => b.getAttribute('aria-label') ?? '')");
        var rotulo = rotulos.GroupBy(r => r).Where(g => g.Key.Length > 0 && g.Count() == 1).Select(g => g.Key).FirstOrDefault();
        Assert.True(rotulo is not null, $"Ningún trabajador de la lista tiene un rótulo único: {string.Join(" | ", rotulos)}");
        await page.Locator("tbody tr.fila-pulsable").GetByRole(AriaRole.Button,
            new LocatorGetByRoleOptions { Name = rotulo, Exact = true }).ClickAsync();
        var panel = page.Locator(".workspace-panel");
        await panel.Locator("button[aria-label='Editar información del trabajador']").ClickAsync();
        var alias = panel.GetByLabel("Alias", new LocatorGetByLabelOptions { Exact = true });
        const string sinGuardar = "Alias sin guardar";
        await alias.FillAsync(sinGuardar);
        await panel.GetByLabel("Apellidos", new LocatorGetByLabelOptions { Exact = true }).FocusAsync(); // El foco sale y notifica el cambio.
        await Expect(page).ToHaveURLAsync(new Regex(@"/trabajadores\?ctx="));
        var urlDeLaVista = UrlExacta(page.Url);

        await AtrasAsync(page);

        await Expect(Pregunta(page)).ToBeVisibleAsync();
        await Expect(page).ToHaveURLAsync(urlDeLaVista);
        await Expect(alias).ToHaveValueAsync(sinGuardar);
        await ResponderAsync(page, "Seguir editando");
        await Expect(page).ToHaveURLAsync(urlDeLaVista);
        await Expect(alias).ToHaveValueAsync(sinGuardar);

        await EnlaceDelMenu(page, "centros").ClickAsync();

        await Expect(Pregunta(page)).ToBeVisibleAsync();
        await Expect(page).ToHaveURLAsync(urlDeLaVista);
        await ResponderAsync(page, "Seguir editando");
        await Expect(page).ToHaveURLAsync(urlDeLaVista);
        await Expect(alias).ToHaveValueAsync(sinGuardar);

        // La otra respuesta: «atrás» se repite y llega a la pantalla anterior, sin la ficha.
        await AtrasAsync(page);
        await Expect(Pregunta(page)).ToBeVisibleAsync();
        await ResponderAsync(page, "Salir y descartar");
        await Expect(page).ToHaveURLAsync(new Regex(@"/centros$"));
        await Expect(page).ToHaveTitleAsync(new Regex("^Centros"));
        await Expect(panel.GetByLabel("Alias", new LocatorGetByLabelOptions { Exact = true })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Centros_con_el_alta_a_medias_atras_pregunta_seguir_editando_conserva_la_edicion_y_la_url_y_sin_cambios_no_pregunta()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await EntrarAsync(contexto, "trabajadores");
        await EnlaceDelMenu(page, "centros").ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex(@"/centros$"));
        await Expect(page).ToHaveTitleAsync(new Regex("^Centros"));

        // Sin nada a medias, «atrás» y «adelante» navegan como siempre: el aviso no se pone en medio.
        await AtrasAsync(page);
        await Expect(page).ToHaveURLAsync(new Regex(@"/trabajadores$"));
        await Expect(page).ToHaveTitleAsync(new Regex("trabajadores", RegexOptions.IgnoreCase));
        await page.EvaluateAsync("() => history.forward()");
        await Expect(page).ToHaveURLAsync(new Regex(@"/centros$"));
        await Expect(page).ToHaveTitleAsync(new Regex("^Centros"));
        await Expect(Pregunta(page)).ToHaveCountAsync(0);

        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "+ Nuevo centro", Exact = true }).ClickAsync();
        var drawer = page.Locator(".drawer-panel");
        var nombre = drawer.GetByLabel("Nombre", new LocatorGetByLabelOptions { Exact = true });
        const string sinGuardar = "Centro sin guardar";
        await nombre.FillAsync(sinGuardar);
        await drawer.GetByLabel("Dirección", new LocatorGetByLabelOptions { Exact = true }).FocusAsync(); // El foco sale y notifica el cambio.
        var urlDeLaVista = UrlExacta(page.Url);

        await AtrasAsync(page);

        await Expect(Pregunta(page)).ToBeVisibleAsync();
        await Expect(page).ToHaveURLAsync(urlDeLaVista);
        await Expect(nombre).ToHaveValueAsync(sinGuardar);
        await ResponderAsync(page, "Seguir editando");
        await Expect(page).ToHaveURLAsync(urlDeLaVista);
        await Expect(page).ToHaveTitleAsync(new Regex("^Centros"));
        await Expect(nombre).ToHaveValueAsync(sinGuardar);

        await AtrasAsync(page);
        await Expect(Pregunta(page)).ToBeVisibleAsync();
        await ResponderAsync(page, "Salir y descartar");
        await Expect(page).ToHaveURLAsync(new Regex(@"/trabajadores$"));
        await Expect(page).ToHaveTitleAsync(new Regex("trabajadores", RegexOptions.IgnoreCase));
        await Expect(page.Locator("tbody tr.fila-pulsable")).Not.ToHaveCountAsync(0);
    }
}
