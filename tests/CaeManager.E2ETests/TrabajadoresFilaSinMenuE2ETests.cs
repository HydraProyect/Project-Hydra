using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Patrón de listados sin menú «⋯» (decisión 2026-10-08), en Trabajadores. La lista es un
/// QuickGrid, que no ofrece clic de fila: la fila lleva «fila-pulsable» y es
/// <c>atajos-lista.js</c> quien, ante un clic en cualquier punto que no sea un control, pulsa
/// el nombre de la fila. Esa mitad no existe para bUnit: solo se prueba aquí, en un navegador
/// real, junto con que los controles de dentro (DNI copiable, icono 360) no abren la vista
/// rápida. El formulario de edición lo prueba <c>TrabajadorWorkspacePanelLapizTests</c> (bUnit).
/// </summary>
[Collection("AppCollection")]
public class TrabajadoresFilaSinMenuE2ETests(WebAppFixture fixture)
{
    private const string Lapiz = "button[aria-label='Editar información del trabajador']";

    // La lista sembrada no tiene un trabajador de nombre estable que sirva de ancla: el
    // recorrido vale para cualquier fila, así que se toma la primera, y su celda «Empresa» (la
    // segunda) como punto de la fila que no es ningún control.
    private static ILocator PrimeraFila(IPage page) => page.Locator("tbody tr.fila-pulsable").First;
    private static ILocator CeldaSinControles(ILocator fila) => fila.Locator("td:nth-child(2)");

    private async Task<IPage> AbrirTrabajadoresAsync(IBrowserContext contexto)
    {
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 800);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
        await Expect(page.Locator("tbody tr.fila-pulsable")).Not.ToHaveCountAsync(0);
        return page;
    }

    /// <summary>
    /// «j» sin fila enfocada enfoca la primera. Se reintenta porque QuickGrid vuelve a pedir la
    /// página por su cuenta tras el primer pintado cuando el total cambia (ver
    /// <c>Trabajadores.RecargarAsync</c>), y cada carga limpia la fila enfocada: una «j» que
    /// llegue antes de esa segunda carga pierde el foco sin que nada en el DOM lo anuncie.
    /// Mientras no haya fila enfocada, repetir «j» vuelve a la primera, así que el reintento no
    /// cambia qué fila se prueba.
    /// </summary>
    private static async Task EnfocarPrimeraFilaConJAsync(IPage page)
    {
        var enfocada = page.Locator("tbody tr.fila-enfocada");
        for (var intento = 0; intento < 5; intento++)
        {
            if (await enfocada.CountAsync() == 0)
                await page.Keyboard.PressAsync("j");
            await page.WaitForTimeoutAsync(400);
            if (await enfocada.CountAsync() == 1 && await PrimeraFila(page).EvaluateAsync<bool>("f => f.classList.contains('fila-enfocada')"))
            {
                await page.WaitForTimeoutAsync(600);
                if (await enfocada.CountAsync() == 1) return;
            }
        }

        await Expect(enfocada).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task El_clic_en_la_fila_abre_la_vista_rapida_los_controles_no_y_el_lapiz_edita()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        await contexto.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        var page = await AbrirTrabajadoresAsync(contexto);
        var fila = PrimeraFila(page);
        var nombre = (await fila.Locator(".nombre-abre-vista-rapida").InnerTextAsync()).Trim();
        var panel = page.Locator(".workspace-panel");

        await Expect(page.Locator("tbody .menu-acciones-disparador")).ToHaveCountAsync(0);

        // El DNI se copia y NO abre el panel: es un control de dentro de la fila.
        var dni = fila.Locator(".boton-copiar-en-linea");
        var valorDni = (await dni.InnerTextAsync()).Trim();
        await dni.ClickAsync();
        await Expect(page.Locator(".toast")).ToContainTextAsync("Se copió el DNI");
        Assert.Equal(valorDni, await page.EvaluateAsync<string>("navigator.clipboard.readText()"));
        await Expect(panel).ToHaveCountAsync(0);

        // El icono 360 de la fila lleva a la página, sin abrir la vista rápida por el camino
        // (si el clic llegara a la fila, la URL de destino llevaría el «ctx» del panel). Con el
        // panel cerrado: abierto, a este ancho tapa el final de la fila.
        await fila.Locator("a.boton-360-pagina").ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/trabajadores/[0-9a-f-]{36}$"));
        await Expect(panel).ToHaveCountAsync(0);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");

        // Un punto de la fila que no es ningún control: la celda «Empresa».
        await CeldaSinControles(fila).ClickAsync();
        await Expect(panel.Locator(".workspace-titulo-entidad")).ToHaveTextAsync(nombre);

        // La cabecera del panel lleva su propio icono 360 a la página completa y el lápiz.
        await Expect(panel.Locator("a.boton-360-pagina")).ToHaveAttributeAsync("href", new Regex(@"^/trabajadores/[0-9a-f-]{36}$"));
        await panel.Locator(Lapiz).ClickAsync();
        await Expect(panel.Locator(".workspace-acciones-edicion")).ToBeVisibleAsync();
        await Expect(panel.Locator(Lapiz)).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Enter_abre_la_vista_rapida_de_la_fila_enfocada_y_la_tecla_e_la_abre_en_edicion()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirTrabajadoresAsync(contexto);
        var panel = page.Locator(".workspace-panel");

        await EnfocarPrimeraFilaConJAsync(page);
        var nombre = (await page.Locator("tbody tr.fila-enfocada .nombre-abre-vista-rapida").InnerTextAsync()).Trim();

        await page.Keyboard.PressAsync("Enter");
        await Expect(panel.Locator(".workspace-titulo-entidad")).ToHaveTextAsync(nombre);
        await Expect(panel.Locator(".workspace-acciones-edicion")).ToHaveCountAsync(0);

        // De vuelta en la lista sin panel, «e» sobre la fila enfocada abre la ficha ya en edición.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
        await Expect(panel).ToHaveCountAsync(0);
        await EnfocarPrimeraFilaConJAsync(page);
        await page.Keyboard.PressAsync("e");

        await Expect(panel.Locator(".workspace-acciones-edicion")).ToBeVisibleAsync();
    }
}
