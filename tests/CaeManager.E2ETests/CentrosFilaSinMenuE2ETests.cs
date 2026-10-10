using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Patrón de listados sin menú «⋯» (decisión 2026-10-08), en Centros: un clic en la fila abre la
/// vista rápida, el lápiz de la cabecera del panel edita —desde cualquier pestaña—, el icono 360
/// lleva a la página completa y la tecla «e» edita la fila enfocada. Lo que bUnit no ve y aquí sí:
/// que el clic de un control de dentro de la fila cuyo manejador repinta la lista (el desplegable
/// de asignaciones) no llega a la fila en un navegador real.
/// <para>
/// Que «e» no pise el atajo global «g e» es del módulo de atajos, común a todas las listas: lo
/// prueban <c>EmpresasFilaSinMenuE2ETests</c> y <c>AtajosSuperficiesTests</c>.
/// </para>
/// </summary>
[Collection("AppCollectionListados")]
public class CentrosFilaSinMenuE2ETests(WebAppFixtureListados fixture)
{
    private const string Lapiz = "button[aria-label='Editar información del centro']";

    // La lista sembrada no tiene un Centro de nombre estable que sirva de ancla: el recorrido vale
    // para cualquier fila, así que se toma la primera, y su celda de Empresa como punto de la fila
    // que no es ningún control.
    private static ILocator PrimeraFila(IPage page) => page.Locator(".lista-filas-acordeon .fila-pulsable").First;
    private static ILocator CeldaSinControles(ILocator fila) => fila.Locator(".columna-empresa-centro");

    private async Task<IPage> AbrirCentrosAsync(IBrowserContext contexto)
    {
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 800);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await IrALaListaAsync(page);
        return page;
    }

    private async Task IrALaListaAsync(IPage page)
    {
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/centros");
        await Ayudas.MostrarCentrosSinAgruparAsync(page);
        await Expect(page.Locator(".lista-filas-acordeon .fila-pulsable")).Not.ToHaveCountAsync(0);
    }

    [Fact]
    public async Task El_clic_en_la_fila_abre_la_vista_rapida_el_lapiz_edita_y_el_icono_360_navega()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirCentrosAsync(contexto);
        var fila = PrimeraFila(page);
        var nombre = (await fila.Locator(".enlace-nombre-fila").InnerTextAsync()).Trim();
        var panel = page.Locator(".workspace-panel");

        await Expect(page.Locator(".lista-filas-acordeon .menu-acciones-disparador")).ToHaveCountAsync(0);

        // Un control de dentro (el desplegable de asignaciones) hace lo suyo y NO abre el panel.
        // Barrera de la ausencia: el panel se abriría en el mismo viaje al servidor que despliega
        // la fila, así que tras un segundo viaje completo (plegarla) ya estaría pintado.
        var desplegable = fila.Locator(".boton-expandir-fila");
        await desplegable.ClickAsync();
        await Expect(desplegable).ToHaveAttributeAsync("aria-expanded", "true");
        await desplegable.ClickAsync();
        await Expect(desplegable).ToHaveAttributeAsync("aria-expanded", "false");
        await Expect(panel).ToHaveCountAsync(0);

        // Un punto de la fila que no es ningún control: la celda de la Empresa.
        await CeldaSinControles(fila).ClickAsync();
        await Expect(panel.Locator(".workspace-titulo-entidad")).ToContainTextAsync(nombre);

        // La cabecera del panel lleva el icono 360 (enlace a la página completa) y el lápiz, que
        // entra en edición sin salir de la lista: aparece el formulario de «Información».
        await Expect(panel.Locator("a.boton-360-pagina")).ToHaveAttributeAsync("href", new Regex(@"^/centros/[0-9a-f-]{36}$"));
        await Expect(panel.GetByLabel("Nombre", new() { Exact = true })).ToHaveCountAsync(0);
        await panel.Locator(Lapiz).ClickAsync();
        await Expect(panel.GetByLabel("Nombre", new() { Exact = true })).ToHaveValueAsync(nombre);
        await Expect(page).ToHaveURLAsync(new Regex(@"/centros(\?|$)"));

        // El icono 360 de la fila lleva a la página, sin abrir la vista rápida por el camino.
        // Con el panel cerrado: abierto, a este ancho tapa el final de la fila, y su cabecera
        // tiene su propio icono 360.
        await IrALaListaAsync(page);
        await Expect(panel).ToHaveCountAsync(0);
        await fila.Locator("a.boton-360-pagina").ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/centros/[0-9a-f-]{36}$"));
        await Expect(panel).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task La_tecla_e_abre_la_fila_enfocada_ya_en_edicion()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirCentrosAsync(contexto);
        var panel = page.Locator(".workspace-panel");

        await page.Keyboard.PressAsync("j");
        var enfocada = page.Locator(".lista-filas-acordeon .fila-enfocada");
        await Expect(enfocada).ToHaveCountAsync(1);
        var nombre = (await enfocada.Locator(".enlace-nombre-fila").InnerTextAsync()).Trim();
        await Expect(panel).ToHaveCountAsync(0);

        await page.Keyboard.PressAsync("e");

        await Expect(panel.GetByLabel("Nombre", new() { Exact = true })).ToHaveValueAsync(nombre);
        await Expect(panel.Locator(Lapiz)).ToHaveCountAsync(0);
    }
}
