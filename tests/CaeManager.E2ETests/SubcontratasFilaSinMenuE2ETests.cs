using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Patrón de listados sin menú «⋯» (decisión 2026-10-08), en Subcontratas: un clic en la fila
/// abre la vista rápida —el panel del Context Workspace—, el lápiz de la cabecera del panel
/// edita, el icono 360 lleva a la página Subcontrata 360 y la tecla «e» edita la fila enfocada.
/// Lo que bUnit no ve y aquí sí: que el clic de un control de dentro de la fila cuyo manejador
/// repinta la lista (el desplegable) no llega a la fila en un navegador real.
/// <para>
/// Editar una Subcontrata carga las credenciales de su portal, que exigen autenticación en dos
/// pasos. El usuario sembrado no la tiene, así que entrar en edición se observa aquí por su
/// efecto: la aplicación lo lleva a configurarla (<c>/cuenta/configurar-2fa?motivo=credenciales</c>).
/// El formulario en sí lo prueba <c>SubcontrataWorkspacePanelLapizTests</c> (bUnit).
/// </para>
/// </summary>
[Collection("AppCollection")]
public class SubcontratasFilaSinMenuE2ETests(WebAppFixture fixture)
{
    private const string Lapiz = "button[aria-label='Editar la información de la subcontrata']";
    private static readonly Regex EntrarEnEdicion = new(@"/cuenta/configurar-2fa\?motivo=credenciales");
    private static readonly Regex PaginaSubcontrata360 = new(@"/subcontratas/[0-9a-f-]{36}$");

    // El recorrido vale para cualquier fila de la lista sembrada, así que se toma la primera, y
    // su celda de nivel de servicio (la tercera) como punto de la fila que no es ningún control.
    private static ILocator PrimeraFila(IPage page) => page.Locator(".lista-filas-acordeon .fila-pulsable").First;
    private static ILocator CeldaSinControles(ILocator fila) => fila.Locator(":scope > :nth-child(3)");

    private async Task<IPage> AbrirSubcontratasAsync(IBrowserContext contexto)
    {
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 800);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/subcontratas");
        await Expect(page.Locator(".lista-filas-acordeon .fila-pulsable")).Not.ToHaveCountAsync(0);
        return page;
    }

    [Fact]
    public async Task El_clic_en_la_fila_abre_la_vista_rapida_el_lapiz_edita_y_el_icono_360_navega()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirSubcontratasAsync(contexto);
        var fila = PrimeraFila(page);
        var nombre = (await fila.Locator(".enlace-nombre-fila").InnerTextAsync()).Trim();
        var panel = page.Locator(".workspace-panel");

        await Expect(page.Locator(".lista-filas-acordeon .menu-acciones-disparador")).ToHaveCountAsync(0);

        // Un control de dentro (el desplegable de trabajadores) hace lo suyo y NO abre el panel.
        await fila.Locator(".boton-expandir-fila").ClickAsync();
        await Expect(fila.Locator(".boton-expandir-fila")).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(panel).ToHaveCountAsync(0);
        await fila.Locator(".boton-expandir-fila").ClickAsync();
        await Expect(fila.Locator(".boton-expandir-fila")).ToHaveAttributeAsync("aria-expanded", "false");
        await Expect(panel).ToHaveCountAsync(0);

        // Un punto de la fila que no es ningún control: la celda de nivel de servicio.
        await CeldaSinControles(fila).ClickAsync();
        await Expect(panel.Locator(".workspace-titulo-entidad")).ToContainTextAsync(nombre);

        // El icono 360 de la cabecera del panel es un enlace a la página completa.
        await Expect(panel.Locator("a.boton-360-pagina")).ToHaveAttributeAsync("href", PaginaSubcontrata360);

        // El icono 360 de la fila lleva a la página, sin abrir la vista rápida por el camino.
        // Con el panel cerrado: abierto, a este ancho tapa el final de la fila, y su cabecera
        // tiene su propio icono 360.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/subcontratas");
        await Expect(panel).ToHaveCountAsync(0);
        await fila.Locator("a.boton-360-pagina").ClickAsync();
        await page.WaitForURLAsync(PaginaSubcontrata360);
        await Expect(panel).ToHaveCountAsync(0);

        // De vuelta en la lista, el lápiz de la cabecera del panel entra en edición. Va el
        // último: en el usuario sembrado, editar saca de la lista a configurar la 2FA.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/subcontratas");
        await CeldaSinControles(fila).ClickAsync();
        await panel.Locator(Lapiz).ClickAsync();
        await page.WaitForURLAsync(EntrarEnEdicion);
    }

    [Fact]
    public async Task La_tecla_e_edita_la_fila_enfocada()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirSubcontratasAsync(contexto);

        await page.Keyboard.PressAsync("j");
        await Expect(page.Locator(".lista-filas-acordeon .fila-enfocada")).ToHaveCountAsync(1);
        await page.Keyboard.PressAsync("e");

        await page.WaitForURLAsync(EntrarEnEdicion);
    }
}
