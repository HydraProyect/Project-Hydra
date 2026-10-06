using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

[Collection("AppCollection")]
public class GestionesFase1TecladoTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Opciones_Centro_y_teclado_nativo_conservan_foco_e_identidad_de_la_fila()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl,
            Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/gestiones");
        var filas = page.Locator("tbody tr").Filter(new LocatorFilterOptions
        { Has = page.Locator(".gestion-trabajador-centro") });
        // Posiciones semánticas: j/j/k recorre primera, segunda y primera fila del orden actual.
        var primeraFila = filas.Nth(0);
        var segundaFila = filas.Nth(1);
        await Expect(segundaFila).ToBeVisibleAsync(); // Precondición explícita: al menos dos filas.
        var cabecera = page.GetByRole(AriaRole.Columnheader,
            new PageGetByRoleOptions { NameRegex = new Regex(@"\bTrabajador\b") });
        Assert.True(await cabecera.CountAsync() == 1, await page.Locator("thead").AriaSnapshotAsync());
        await cabecera.Locator(".col-options-button").ClickAsync();
        var opcionCentro = cabecera.Locator(".col-options").GetByRole(AriaRole.Button,
            new LocatorGetByRoleOptions { Name = "Ordenar por centro", Exact = true });
        await opcionCentro.FocusAsync();
        await page.EvaluateAsync("""
            () => {
                window.__gestionesFocoDuranteClick = { clic: false, foco: false };
                document.addEventListener('click', evento => {
                    const opcion = evento.target.closest('.gestiones-opciones-orden button');
                    if (!opcion) return;
                    const titulo = opcion.closest('th').querySelector('button.col-title');
                    window.__gestionesFocoDuranteClick = { clic: true, foco: document.activeElement === titulo };
                }, { once: true });
            }
            """);
        await opcionCentro.PressAsync("Space");
        Assert.True(await page.EvaluateAsync<bool>("window.__gestionesFocoDuranteClick.clic"),
            "Control positivo: Space produjo el clic de la opción Centro.");
        Assert.True(await page.EvaluateAsync<bool>("window.__gestionesFocoDuranteClick.foco"),
            "Foco nativo de orden ausente durante el clic físico de la opción Centro.");
        await cabecera.Locator(".col-options").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
        await Expect(cabecera.Locator("button.col-title")).ToBeFocusedAsync();
        await Expect(segundaFila).ToBeVisibleAsync();
        var nombre1 = (await primeraFila.Locator(".gestion-trabajador-centro button:not(.gestion-centro)").InnerTextAsync()).Trim();
        var nombre2 = (await segundaFila.Locator(".gestion-trabajador-centro button:not(.gestion-centro)").InnerTextAsync()).Trim();
        var centro2 = (await segundaFila.Locator(".gestion-centro").InnerTextAsync()).Trim();
        var centro1 = (await primeraFila.Locator(".gestion-centro").InnerTextAsync()).Trim();
        var tipo1 = (await primeraFila.Locator(".gestion-tipo-documento").InnerTextAsync()).Trim();
        var tipo2 = (await segundaFila.Locator(".gestion-tipo-documento").InnerTextAsync()).Trim();
        Assert.False(string.IsNullOrWhiteSpace(nombre1) || string.IsNullOrWhiteSpace(nombre2)
            || string.IsNullOrWhiteSpace(centro1) || string.IsNullOrWhiteSpace(centro2)
            || string.IsNullOrWhiteSpace(tipo1) || string.IsNullOrWhiteSpace(tipo2),
            "Control positivo: las dos filas traen Trabajador, Centro y tipo de documento.");
        Assert.NotEqual((nombre1, centro1, tipo1), (nombre2, centro2, tipo2));
        // La tupla debe distinguir las dos filas; no se afirma que sea una clave del dominio.

        // F es tecla física y el input está fuera del circuito bUnit.
        await page.Keyboard.PressAsync("f");
        var buscador = page.GetByPlaceholder("Filtrar esta pantalla: trabajador, centro o tipo de documento");
        await Expect(buscador).ToBeFocusedAsync();
        await cabecera.Locator("button.col-title").FocusAsync(); // Salir del input sin cambiar query.
        await page.Keyboard.PressAsync("j");
        await Expect(primeraFila).ToHaveClassAsync(new Regex(@"\bfila-enfocada\b"));
        await Expect(primeraFila).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("x");
        var panel = page.Locator(".vista-rapida-gestion");
        await Expect(panel).ToHaveCountAsync(0);
        await page.Keyboard.PressAsync("j");
        await Expect(segundaFila).ToBeFocusedAsync();
        await Expect(segundaFila).ToHaveClassAsync(new Regex(@"\bfila-enfocada\b"));
        await page.Keyboard.PressAsync("k");
        await Expect(primeraFila).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("j");
        await Expect(segundaFila).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Enter");
        await Expect(panel.Locator(".nombre-vista-rapida-gestion")).ToHaveTextAsync(nombre2);
        await Expect(panel.Locator(".vista-rapida-centro strong"))
            .ToHaveTextAsync(centro2);
        await Expect(panel.Locator(".vista-rapida-tipo-documento strong"))
            .ToHaveTextAsync(tipo2);
        await panel.Locator(".boton-cerrar-vista-rapida-gestion").ClickAsync();
        await Expect(panel).ToHaveCountAsync(0);
        await segundaFila.Locator(".gestion-centro").ClickAsync();
        await Expect(panel.Locator(".nombre-vista-rapida-gestion")).ToHaveTextAsync(nombre2);
        await Expect(panel.Locator(".vista-rapida-centro strong"))
            .ToHaveTextAsync(centro2);
        await Expect(panel.Locator(".vista-rapida-tipo-documento strong"))
            .ToHaveTextAsync(tipo2);
    }
}

