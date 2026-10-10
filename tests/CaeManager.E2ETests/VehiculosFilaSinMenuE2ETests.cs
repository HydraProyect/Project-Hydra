using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Patrón de listados sin menú «⋯» (decisión 2026-10-08), en Vehículos. La lista es un
/// QuickGrid, que no ofrece clic de fila: la fila lleva «fila-pulsable» y es
/// <c>atajos-lista.js</c> quien, ante un clic en cualquier punto que no sea un control, pulsa
/// el nombre de la fila. Esa mitad no existe para bUnit: solo se prueba aquí, en un navegador
/// real, junto con que los controles de dentro (matrícula copiable, icono 360) no abren la
/// vista rápida. El formulario de edición lo prueba <c>Vehiculo360Gen2Tests</c> (bUnit).
/// </summary>
[Collection("AppCollectionListados")]
public class VehiculosFilaSinMenuE2ETests(WebAppFixtureListados fixture)
{
    private const string Lapiz = "button[aria-label='Editar información del vehículo']";

    /// <summary>
    /// Abre /vehiculos, da de alta un vehículo propio del test y filtra la lista por él: queda
    /// una sola fila, así que ningún localizador depende de la siembra.
    /// </summary>
    private async Task<(IPage Page, ILocator Fila, string Nombre, string Matricula)> AbrirConUnVehiculoAsync(IBrowserContext contexto)
    {
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 800);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/vehiculos");
        var prefijo = $"FSM-{Guid.NewGuid().ToString("N")[..8]}";
        var nombre = $"{prefijo} Furgoneta";
        var matricula = $"{prefijo}-M";
        await ListadosFase1VehiculosDocumentosTests.CrearVehiculoAsync(page, nombre, "Modelo de prueba", matricula);
        var fila = await FiltrarAsync(page, prefijo);
        await Expect(fila.Locator(".enlace-nombre-fila")).ToHaveTextAsync(nombre);
        return (page, fila, nombre, matricula);
    }

    private static async Task<ILocator> FiltrarAsync(IPage page, string busqueda)
    {
        await page.GetByPlaceholder("Filtrar esta pantalla: nombre, modelo o matrícula").FillAsync(busqueda);
        // La fila se localiza por su celda, no por «fila-pulsable»: si la marca faltara, lo que
        // debe fallar es el clic en la fila, no la preparación.
        var fila = page.Locator("tbody tr").Filter(new LocatorFilterOptions { Has = page.Locator(".celda-vehiculo") });
        await Expect(fila).ToHaveCountAsync(1);
        return fila;
    }

    /// <summary>
    /// «j» enfoca la única fila. Se reintenta porque QuickGrid vuelve a pedir la página por su
    /// cuenta cuando el total cambia (aquí, al filtrar), y cada carga limpia la fila enfocada:
    /// una «j» que llegue antes de esa segunda carga pierde el foco sin que nada en el DOM lo
    /// anuncie. El foco se saca antes del campo de filtro, donde «j» sería texto.
    /// </summary>
    private static async Task EnfocarLaFilaConJAsync(IPage page)
    {
        var enfocada = page.Locator("tbody tr.fila-enfocada");
        for (var intento = 0; intento < 5; intento++)
        {
            if (await enfocada.CountAsync() == 0)
            {
                await page.Locator("thead th.col-estado button.col-title").FocusAsync();
                await page.Keyboard.PressAsync("j");
            }

            await page.WaitForTimeoutAsync(400);
            if (await enfocada.CountAsync() == 1)
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
        var (page, fila, nombre, matricula) = await AbrirConUnVehiculoAsync(contexto);
        var panel = page.Locator(".workspace-panel");

        await Expect(page.Locator("tbody .menu-acciones-disparador")).ToHaveCountAsync(0);

        // La matrícula se copia y NO abre el panel: es un control de dentro de la fila.
        await fila.Locator(".boton-copiar-en-linea").ClickAsync();
        // Se observa el portapapeles y no el aviso: el del alta recién hecha sigue en pantalla.
        await page.WaitForFunctionAsync("async esperado => (await navigator.clipboard.readText()) === esperado", matricula);
        await Expect(panel).ToHaveCountAsync(0);

        // Un punto de la fila que no es ningún control: la celda de estado documental.
        await fila.Locator("td.col-estado").ClickAsync();
        await Expect(panel.Locator(".workspace-titulo-entidad")).ToHaveTextAsync(nombre);

        // La cabecera del panel lleva su propio icono 360 a la página completa y el lápiz.
        await Expect(panel.Locator("a.boton-360-pagina")).ToHaveAttributeAsync("href", new Regex(@"^/vehiculos/[0-9a-f-]{36}$"));
        await panel.Locator(Lapiz).ClickAsync();
        await Expect(panel.Locator(".workspace-acciones-edicion")).ToBeVisibleAsync();
        await Expect(panel.Locator(Lapiz)).ToHaveCountAsync(0);
    }

    /// <summary>
    /// El icono 360 de la fila lleva a la página sin abrir la vista rápida por el camino (si el
    /// clic llegara a la fila, la URL de destino llevaría el «ctx» del panel). Con el panel
    /// cerrado: abierto, a este ancho tapa el final de la fila.
    /// </summary>
    [Fact]
    public async Task El_icono_360_de_la_fila_lleva_a_la_pagina_sin_abrir_la_vista_rapida()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var (page, fila, _, _) = await AbrirConUnVehiculoAsync(contexto);

        await fila.Locator("a.boton-360-pagina").ClickAsync();

        await page.WaitForURLAsync(new Regex(@"/vehiculos/[0-9a-f-]{36}$"));
        await Expect(page.Locator(".workspace-panel")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Enter_abre_la_vista_rapida_de_la_fila_enfocada_y_la_tecla_e_la_abre_en_edicion()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var (page, _, nombre, _) = await AbrirConUnVehiculoAsync(contexto);
        var panel = page.Locator(".workspace-panel");

        await EnfocarLaFilaConJAsync(page);
        await page.Keyboard.PressAsync("Enter");
        await Expect(panel.Locator(".workspace-titulo-entidad")).ToHaveTextAsync(nombre);
        await Expect(panel.Locator(".workspace-acciones-edicion")).ToHaveCountAsync(0);

        // De vuelta en la lista sin panel, «e» sobre la fila enfocada abre la ficha ya en edición.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/vehiculos");
        await Expect(panel).ToHaveCountAsync(0);
        await FiltrarAsync(page, nombre);
        await EnfocarLaFilaConJAsync(page);
        await page.Keyboard.PressAsync("e");

        await Expect(panel.Locator(".workspace-titulo-entidad")).ToHaveTextAsync(nombre);
        await Expect(panel.Locator(".workspace-acciones-edicion")).ToBeVisibleAsync();
    }
}
