using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// KeyTips sobre un listado real (Centros): las letras las declaran los componentes compartidos
/// (CabeceraListado, BarraFiltros) y la propia página («Agrupar»), y los menús los abre Blazor
/// por el circuito, que es justo lo que <see cref="KeyTipsSuperficieTests"/> solo puede imitar.
/// Un único recorrido encadenado: encender, bajar a un grupo y elegir, subir, abrir un menú,
/// ejecutar una letra y salir.
/// </summary>
/// <remarks>
/// El «grupo» es «Agrupar» porque la franja de estado (letra T) todavía no existe en el código:
/// cuando llegue se declara igual (<c>data-keytip="T" data-keytip-grupo</c>) y baja de nivel
/// por este mismo camino.
/// </remarks>
[Collection("AppCollection")]
public class KeyTipsEnListadoTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Alt_enciende_las_letras_baja_a_un_grupo_abre_un_menu_ejecuta_y_sale_en_Centros()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        // El Gestor CAE de la demo tiene Asignación de Cartera y centros sembrados: «Agrupar» solo
        // se pinta con centros en la página, y el Administrador inicial no tiene ninguno propio.
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailGestorRefrielectric, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/centros");

        var html = page.Locator("html");
        var letras = page.Locator(".keytip");
        var agrupar = page.Locator("[data-keytip-grupo]");
        var sinAgrupar = agrupar.GetByRole(AriaRole.Button, new() { Name = "Sin agrupar", Exact = true });
        var filtro = page.Locator("[data-filtro-pantalla]");

        // «Agrupar» solo se pinta con centros en la página: sin él no hay grupo al que bajar.
        await Assertions.Expect(agrupar).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(sinAgrupar).ToHaveAttributeAsync("aria-pressed", "false");

        // --- Encender: Alt pulsada y soltada sola. ---
        await page.Keyboard.PressAsync("Alt");
        await Assertions.Expect(html).ToHaveAttributeAsync("data-keytips", "raiz");
        foreach (var letra in new[] { "K", "F", "L", "A" })
            await Assertions.Expect(letras.GetByText(letra, new() { Exact = true })).ToHaveCountAsync(1);

        // --- Bajar a un grupo y elegir dentro: el modo sigue encendido. ---
        await page.Keyboard.PressAsync("a");
        await Assertions.Expect(html).ToHaveAttributeAsync("data-keytips", "grupo");
        await Assertions.Expect(letras).ToHaveCountAsync(2);
        await page.Keyboard.PressAsync("s");
        await Assertions.Expect(sinAgrupar).ToHaveAttributeAsync("aria-pressed", "true");
        await Assertions.Expect(html).ToHaveAttributeAsync("data-keytips", "grupo");

        // --- Retroceso sube a la raíz. ---
        await page.Keyboard.PressAsync("Backspace");
        await Assertions.Expect(html).ToHaveAttributeAsync("data-keytips", "raiz");

        // --- La letra de un menú: lo abre el servidor y las letras pasan a sus opciones. ---
        var masFiltros = page.Locator("[data-keytip='L']");
        await page.Keyboard.PressAsync("l");
        await Assertions.Expect(masFiltros).ToHaveAttributeAsync("aria-expanded", "true");
        await Assertions.Expect(html).ToHaveAttributeAsync("data-keytips", "menu");
        await page.Keyboard.PressAsync("Backspace");
        await Assertions.Expect(masFiltros).ToHaveAttributeAsync("aria-expanded", "false");
        await Assertions.Expect(html).ToHaveAttributeAsync("data-keytips", "raiz");

        // --- Con el modo encendido, «j» no mueve la fila (no es una letra de la raíz). ---
        await page.Keyboard.PressAsync("j");
        await Assertions.Expect(page.Locator(".fila-enfocada")).ToHaveCountAsync(0);

        // --- Ejecutar una letra: F enfoca el filtro de la pantalla, sin escribir, y apaga. ---
        await page.Keyboard.PressAsync("f");
        await Assertions.Expect(filtro).ToBeFocusedAsync();
        await Assertions.Expect(filtro).ToHaveValueAsync(string.Empty);
        await Assertions.Expect(html).Not.ToHaveAttributeAsync("data-keytips", new System.Text.RegularExpressions.Regex(".+"));
        await Assertions.Expect(letras).ToHaveCountAsync(0);

        // --- Dentro del campo, Alt no enciende. Fuera, enciende y Esc sale. ---
        await page.Keyboard.PressAsync("Alt");
        await Assertions.Expect(letras).ToHaveCountAsync(0);
        await page.Locator("h1").ClickAsync();
        await page.Keyboard.PressAsync("Alt");
        await Assertions.Expect(html).ToHaveAttributeAsync("data-keytips", "raiz");
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(letras).ToHaveCountAsync(0);

        // --- Control positivo: apagado, «j» vuelve a ser de la lista. ---
        await page.Keyboard.PressAsync("j");
        await Assertions.Expect(page.Locator(".fila-enfocada")).ToHaveCountAsync(1);
    }
}
