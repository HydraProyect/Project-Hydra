using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Patrón de listados sin menú «⋯» (decisión 2026-10-08), en Gestiones. La lista es un QuickGrid,
/// que no ofrece clic de fila: la fila lleva «fila-pulsable» y es <c>atajos-lista.js</c> quien,
/// ante un clic en cualquier punto que no sea un control, pulsa el botón del nombre. Esa mitad no
/// existe para bUnit: solo se prueba aquí, en un navegador real, junto con que los botones rápidos
/// de la fila («Completar», «Reabrir») hacen lo suyo sin abrir la vista rápida. Una Gestión no
/// tiene página propia (decisión D7): no hay icono 360 que probar.
/// </summary>
[Collection("AppCollection")]
public class GestionesFilaSinMenuE2ETests(WebAppFixture fixture)
{
    private static ILocator VistaRapida(IPage page) =>
        page.GetByRole(AriaRole.Complementary, new PageGetByRoleOptions { Name = "Vista rápida de la gestión", Exact = true });

    private static ILocator Filas(IPage page) =>
        page.Locator("tbody tr").Filter(new LocatorFilterOptions { Has = page.Locator(".gestion-trabajador-centro") });

    private async Task<IPage> AbrirGestionesAsync(IBrowserContext contexto)
    {
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 800);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/gestiones");
        await Expect(Filas(page)).Not.ToHaveCountAsync(0);
        return page;
    }

    /// <summary>Trabajador, Centro y tipo de documento de una fila: lo que la distingue en la siembra.</summary>
    private static async Task<(string Trabajador, string Centro, string Tipo)> IdentidadAsync(ILocator fila) =>
        ((await fila.Locator("button.nombre-abre-vista-rapida").InnerTextAsync()).Trim(),
         (await fila.Locator("button.gestion-centro").InnerTextAsync()).Trim(),
         (await fila.Locator("td.gestion-tipo-documento").InnerTextAsync()).Trim());

    [Fact]
    public async Task El_clic_en_un_punto_sin_controles_de_la_fila_abre_su_vista_rapida_y_Enter_tambien()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirGestionesAsync(contexto);
        var vista = VistaRapida(page);
        var fila = Filas(page).First;
        var (trabajador, centro, tipo) = await IdentidadAsync(fila);

        await Expect(page.Locator("tbody .menu-acciones-disparador")).ToHaveCountAsync(0);
        await Expect(vista).ToHaveCountAsync(0);

        // La celda del tipo de documento es texto: ningún control.
        await fila.Locator("td.gestion-tipo-documento").ClickAsync();
        await Expect(vista.Locator(".nombre-vista-rapida-gestion")).ToHaveTextAsync(trabajador);
        await Expect(vista.Locator(".vista-rapida-centro strong")).ToHaveTextAsync(centro);
        await Expect(vista.Locator(".vista-rapida-tipo-documento strong")).ToHaveTextAsync(tipo);

        await vista.Locator("button.boton-cerrar-vista-rapida-gestion").ClickAsync();
        await Expect(vista).ToHaveCountAsync(0);

        // Enter sobre la fila enfocada. «j» se reintenta: QuickGrid puede volver a pedir la página
        // y limpiar la fila enfocada sin que nada en el DOM lo anuncie.
        var enfocada = page.Locator("tbody tr.fila-enfocada");
        for (var intento = 0; intento < 20; intento++)
        {
            if (await enfocada.CountAsync() == 0)
                await page.Keyboard.PressAsync("j");
            await page.WaitForTimeoutAsync(400);
            if (await enfocada.CountAsync() == 1)
            {
                await page.WaitForTimeoutAsync(600);
                if (await enfocada.CountAsync() == 1) break;
            }
        }

        await Expect(enfocada).ToHaveCountAsync(1);
        var (trabajadorEnfocado, centroEnfocado, _) = await IdentidadAsync(enfocada);
        await page.Keyboard.PressAsync("Enter");
        await Expect(vista.Locator(".nombre-vista-rapida-gestion")).ToHaveTextAsync(trabajadorEnfocado);
        await Expect(vista.Locator(".vista-rapida-centro strong")).ToHaveTextAsync(centroEnfocado);
    }

    /// <summary>
    /// «Completar» y «Reabrir» son controles de dentro de la fila: cambian el estado y NO abren la
    /// vista rápida. El test completa una Gestión pendiente y la reabre, de modo que deja la
    /// siembra como la encontró.
    /// </summary>
    [Fact]
    public async Task Completar_y_Reabrir_cambian_el_estado_de_la_fila_sin_abrir_la_vista_rapida()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirGestionesAsync(contexto);
        var vista = VistaRapida(page);
        var reabrir = page.Locator("tbody button.gestion-reabrir");

        var pendientes = Filas(page).Filter(new LocatorFilterOptions { Has = page.Locator("button.gestion-completar") });
        await Expect(pendientes).Not.ToHaveCountAsync(0);
        var pendiente = pendientes.First;
        var identidad = await IdentidadAsync(pendiente);
        var completadasAntes = await reabrir.CountAsync();

        // El recuento de «Reabrir» es la barrera: sube cuando el cambio ha vuelto del servidor, y
        // para entonces un clic que hubiera llegado a la fila ya habría abierto la vista rápida.
        await pendiente.Locator("button.gestion-completar").ClickAsync();
        await Expect(reabrir).ToHaveCountAsync(completadasAntes + 1);
        await Expect(vista).ToHaveCountAsync(0);

        // La fila completada cambia de sitio (el orden por defecto es el estado): se busca por su
        // identidad entre las que ahora ofrecen «Reabrir».
        var completadas = Filas(page).Filter(new LocatorFilterOptions { Has = page.Locator("button.gestion-reabrir") });
        ILocator? laMisma = null;
        for (var i = 0; i < await completadas.CountAsync(); i++)
        {
            var candidata = completadas.Nth(i);
            if (await IdentidadAsync(candidata) != identidad) continue;
            laMisma = candidata;
            break;
        }

        Assert.True(laMisma is not null, "Control positivo: la Gestión completada sigue en la lista, ahora con «Reabrir».");

        await laMisma!.Locator("button.gestion-reabrir").ClickAsync();
        await Expect(reabrir).ToHaveCountAsync(completadasAntes);
        await Expect(vista).ToHaveCountAsync(0);
    }
}
