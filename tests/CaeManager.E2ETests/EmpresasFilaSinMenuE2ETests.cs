using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Patrón de listados sin menú «⋯» (decisión 2026-10-08), en Empresas: un clic en la fila abre la
/// vista rápida, el lápiz de la cabecera del panel edita, el icono 360 lleva a la página completa
/// y la tecla «e» edita la fila enfocada sin pisar el atajo global «g e». Lo que bUnit no ve y
/// aquí sí: que el clic de un control de dentro de la fila no llega a la fila en un navegador
/// real, y que el módulo de atajos distingue «e» de «g e».
/// <para>
/// Editar una Empresa es la puerta a las credenciales de la Plataforma CAE, que exigen
/// autenticación en dos pasos. El usuario sembrado no la tiene, así que entrar en edición se
/// observa aquí por su efecto: la aplicación lo lleva a configurarla
/// (<c>/cuenta/configurar-2fa?motivo=credenciales</c>). El formulario en sí lo prueba
/// <c>EmpresaWorkspacePanelLapizTests</c> (bUnit).
/// </para>
/// </summary>
[Collection("AppCollectionListados")]
public class EmpresasFilaSinMenuE2ETests(WebAppFixtureListados fixture)
{
    private const string Lapiz = "button[aria-label='Editar información de la empresa']";
    private static readonly Regex EntrarEnEdicion = new(@"/cuenta/configurar-2fa\?motivo=credenciales");

    // La lista sembrada no tiene una Empresa de nombre estable que sirva de ancla: el recorrido
    // vale para cualquier fila, así que se toma la primera, y su celda de cumplimiento (la
    // tercera) como punto de la fila que no es ningún control.
    private static ILocator PrimeraFila(IPage page) => page.Locator(".marco-lista-empresas .fila-pulsable").First;
    private static ILocator CeldaSinControles(ILocator fila) => fila.Locator(":scope > :nth-child(3)");

    private async Task<IPage> AbrirEmpresasAsync(IBrowserContext contexto)
    {
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 800);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/empresas");
        await Expect(page.Locator(".marco-lista-empresas .fila-pulsable")).Not.ToHaveCountAsync(0);
        return page;
    }

    [Fact]
    public async Task El_clic_en_la_fila_abre_la_vista_rapida_el_lapiz_edita_y_el_icono_360_navega()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirEmpresasAsync(contexto);
        var fila = PrimeraFila(page);
        var nombre = (await fila.Locator(".enlace-nombre-fila").InnerTextAsync()).Trim();
        var panel = page.Locator(".workspace-panel");

        await Expect(page.Locator(".marco-lista-empresas .menu-acciones-disparador")).ToHaveCountAsync(0);

        // Un control de dentro (el desplegable) hace lo suyo y NO abre el panel.
        await fila.Locator(".boton-expandir-fila").ClickAsync();
        await Expect(fila.Locator(".boton-expandir-fila")).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(panel).ToHaveCountAsync(0);

        // La pastilla de detecciones navega a la detección y NO abre además la vista rápida
        // (si el clic subiera a la fila, la URL de destino llevaría el «ctx» del panel).
        var pastilla = page.Locator(".marco-lista-empresas button.badge-deteccion").First;
        await Expect(pastilla).ToBeVisibleAsync();
        await pastilla.ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/empresas/[0-9a-f-]{36}/deteccion-trabajadores$"));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(page).ToHaveURLAsync(new Regex(@"/deteccion-trabajadores$"));
        await Expect(panel).ToHaveCountAsync(0);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/empresas");

        // Un punto de la fila que no es ningún control: la celda de cumplimiento.
        await CeldaSinControles(fila).ClickAsync();
        await Expect(panel.Locator(".workspace-titulo-entidad")).ToContainTextAsync(nombre);

        // El icono 360 de la cabecera del panel es un enlace a la página completa.
        await Expect(panel.Locator("a.boton-360-pagina")).ToHaveAttributeAsync("href", new Regex(@"^/empresas/[0-9a-f-]{36}$"));

        // El icono 360 de la fila lleva a la página, sin abrir la vista rápida por el camino.
        // Con el panel cerrado: abierto, a este ancho tapa el final de la fila (medido en CI,
        // «workspace-panel … intercepts pointer events»), y su cabecera tiene su propio icono 360.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/empresas");
        await Expect(panel).ToHaveCountAsync(0);
        await fila.Locator("a.boton-360-pagina").ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/empresas/[0-9a-f-]{36}$"));
        await Expect(panel).ToHaveCountAsync(0);

        // De vuelta en la lista, el lápiz de la cabecera del panel entra en edición. Va el
        // último: en el usuario sembrado, editar saca de la lista a configurar la 2FA.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/empresas");
        await CeldaSinControles(fila).ClickAsync();
        await panel.Locator(Lapiz).ClickAsync();
        await page.WaitForURLAsync(EntrarEnEdicion);
    }

    [Fact]
    public async Task La_tecla_e_edita_la_fila_enfocada_y_g_e_sigue_siendo_el_atajo_global()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirEmpresasAsync(contexto);
        var panel = page.Locator(".workspace-panel");

        await page.Keyboard.PressAsync("j");
        await Expect(page.Locator(".marco-lista-empresas .fila-enfocada")).ToHaveCountAsync(1);

        // «g e» es «ir a Empresas»: la «e» que sigue a una «g» no es la de editar.
        await page.Keyboard.PressAsync("g");
        await page.Keyboard.PressAsync("e");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(panel).ToHaveCountAsync(0);
        await Expect(page).ToHaveURLAsync(new Regex(@"/empresas$"));

        // Barrera: pasada la ventana del prefijo, la misma tecla sí edita.
        await page.WaitForTimeoutAsync(1_200);
        await page.Keyboard.PressAsync("j");
        await Expect(page.Locator(".marco-lista-empresas .fila-enfocada")).ToHaveCountAsync(1);
        await page.Keyboard.PressAsync("e");

        await page.WaitForURLAsync(EntrarEnEdicion);
    }
}
