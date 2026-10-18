using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// KeyTips sobre un listado real (Centros): las letras las declaran los componentes compartidos
/// (CabeceraListado, BarraFiltros) y la propia página («Agrupar»), y los menús los abre Blazor
/// por el circuito, que es justo lo que <see cref="KeyTipsSuperficieTests"/> solo puede imitar.
/// Un único recorrido encadenado: encender, abrir un menú de opciones y elegir, abrir otro menú y
/// subir, ejecutar una letra y salir.
/// </summary>
/// <remarks>
/// «Agrupar» es una pastilla (<c>PastillaFiltro</c> con <c>Keytip="A"</c>, la letra estable que anuncia la
/// chuleta; una pastilla sin letra declarada la deduce del nombre y nunca recibe una estable): un menú que abre Blazor, cuyas
/// opciones reciben letra al llegar el panel y que apaga el modo al elegir. Ningún control del código
/// se declara hoy como grupo (<c>data-keytip-grupo</c>): ese nivel, en el que elegir no apaga, lo
/// cubre <see cref="KeyTipsSuperficieTests"/> con HTML estático.
/// </remarks>
[Collection("AppCollectionListados")]
public class KeyTipsEnListadoTests(WebAppFixtureListados fixture)
{
    [Fact]
    public async Task Alt_enciende_las_letras_elige_en_el_menu_Agrupar_abre_otro_menu_ejecuta_y_sale_en_Centros()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        // El Gestor CAE de la demo tiene Asignación de Cartera y centros sembrados: «Agrupar» solo
        // se pinta con centros en la página, y el Administrador inicial no tiene ninguno propio.
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailGestorRefrielectric, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/centros");

        var html = page.Locator("html");
        var letras = page.Locator(".keytip");
        var agrupar = Ayudas.DesplegableAgrupar(page);
        var filtro = page.Locator("[data-filtro-pantalla]");
        var apagado = new System.Text.RegularExpressions.Regex(".+");

        // «Agrupar» solo se pinta con centros en la página; de serie la lista llega sin agrupar.
        await Assertions.Expect(agrupar).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(agrupar).ToHaveAttributeAsync("aria-expanded", "false");
        await Assertions.Expect(agrupar).ToHaveTextAsync("Agrupar");
        await Assertions.Expect(page.Locator(".cabecera-grupo-centros")).ToHaveCountAsync(0);

        // --- Encender: Alt pulsada y soltada sola. ---
        await page.Keyboard.PressAsync("Alt");
        await Assertions.Expect(html).ToHaveAttributeAsync("data-keytips", "raiz");
        // Las letras se pintan en el mismo paso que el atributo: se leen de la capa, sin
        // localizar por texto.
        var pintadas = (await letras.AllTextContentsAsync()).Select(l => l.Trim()).ToList();
        foreach (var letra in new[] { "K", "F", "G", "A" })
            Assert.Single(pintadas, l => l == letra);

        // --- A abre el desplegable «Agrupar» por el circuito: las letras pasan a sus dos opciones. ---
        await page.Keyboard.PressAsync("a");
        await Assertions.Expect(agrupar).ToHaveAttributeAsync("aria-expanded", "true");
        await Assertions.Expect(html).ToHaveAttributeAsync("data-keytips", "menu");
        await Assertions.Expect(letras).ToHaveCountAsync(2);

        // --- Elegir una opción la aplica y apaga el modo: «Por Cliente» pone los títulos de grupo. ---
        await page.Keyboard.PressAsync("p");
        await Assertions.Expect(agrupar).ToHaveTextAsync("Agrupar: Por Cliente");
        await Assertions.Expect(page.Locator(".cabecera-grupo-centros")).Not.ToHaveCountAsync(0);
        await Assertions.Expect(html).Not.ToHaveAttributeAsync("data-keytips", apagado);
        await Assertions.Expect(letras).ToHaveCountAsync(0);

        // --- Encender otra vez (el foco volvió a la pastilla, que no es un campo de texto). ---
        await page.Keyboard.PressAsync("Alt");
        await Assertions.Expect(html).ToHaveAttributeAsync("data-keytips", "raiz");

        // --- La letra de un menú: lo abre el servidor y las letras pasan a sus opciones. ---
        var filtrosGuardados = page.Locator("[data-keytip='G']");
        await page.Keyboard.PressAsync("g");
        await Assertions.Expect(filtrosGuardados).ToHaveAttributeAsync("aria-expanded", "true");
        await Assertions.Expect(html).ToHaveAttributeAsync("data-keytips", "menu");
        await page.Keyboard.PressAsync("Backspace");
        await Assertions.Expect(filtrosGuardados).ToHaveAttributeAsync("aria-expanded", "false");
        await Assertions.Expect(html).ToHaveAttributeAsync("data-keytips", "raiz");

        // --- Con el modo encendido, «j» no mueve la fila (no es una letra de la raíz). ---
        await page.Keyboard.PressAsync("j");
        await Assertions.Expect(page.Locator(".fila-enfocada")).ToHaveCountAsync(0);

        // --- Ejecutar una letra: F enfoca el filtro de la pantalla, sin escribir, y apaga. ---
        await page.Keyboard.PressAsync("f");
        await Assertions.Expect(filtro).ToBeFocusedAsync();
        await Assertions.Expect(filtro).ToHaveValueAsync(string.Empty);
        await Assertions.Expect(html).Not.ToHaveAttributeAsync("data-keytips", apagado);
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
