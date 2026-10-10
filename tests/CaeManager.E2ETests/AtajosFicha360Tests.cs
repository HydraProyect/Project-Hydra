using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Capa de teclado de las fichas 360 (<c>atajos-ficha.js</c>) sobre una ficha real, Subcontrata
/// 360: no hay componente ni C# de por medio, así que solo un navegador puede probar que las
/// teclas llegan al control que el usuario tiene delante. Un único recorrido encadenado:
/// 1–9 cambian de pestaña, j/k recorren las filas, Alt enseña las pistas (las cifras sobre las
/// pestañas y E sobre el botón de la fila enfocada), «e» pulsa ese botón, y con el diálogo que
/// abre ninguna tecla de ficha actúa.
/// </summary>
/// <remarks>
/// Subcontrata 360 porque su lista de pestañas es hermana del cuerpo de la ficha (no lo envuelve
/// ni vive dentro): es la colocación que una búsqueda por parentesco no encontraba.
/// </remarks>
[Collection("AppCollection")]
public class AtajosFicha360Tests(WebAppFixture fixture)
{
    [Fact]
    public async Task Las_teclas_de_ficha_cambian_de_pestana_recorren_filas_y_pulsan_la_accion_de_la_fila_en_Subcontrata_360()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailGestorRefrielectric, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/subcontratas");

        // Las posiciones (qué pestaña, qué fila) se leen en la página, no con localizadores
        // posicionales: es justo lo que el atajo promete («la siguiente», «la segunda»).
        await page.WaitForSelectorAsync("a[href^='/subcontratas/']", new() { Timeout = 30_000 });
        var rutas = await page.EvaluateAsync<string[]>(
            "() => [...new Set([...document.querySelectorAll(\"a[href^='/subcontratas/']\")].map(e => e.getAttribute('href')))]");

        var html = page.Locator("html");
        var enfocada = page.Locator("[data-fila-enfocada]");
        var dialogo = page.Locator("[role='dialog'][aria-modal='true']");

        // La Subcontrata con algún documento por supervisar es la que tiene filas con botón.
        var encontrada = false;
        foreach (var ruta in rutas)
        {
            await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}{ruta}?pestana=supervision");
            await Assertions.Expect(page.Locator("[data-atajos-ficha]")).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await EsperarAsync(page, $"{PestanaActiva} === 1");
            // La pestaña carga su lista por el circuito: se da margen a que pinte alguna fila.
            try { await EsperarAsync(page, $"{FilaConBoton} >= 0", 5_000); }
            catch (TimeoutException) { continue; }
            encontrada = true;
            break;
        }
        Assert.True(encontrada, "la siembra de demostración debe dejar al menos una Subcontrata con un documento por supervisar: sin fila con botón no se puede probar «e»");

        // --- 1–9 cambian de pestaña (la lista de pestañas es hermana del cuerpo de la ficha). ---
        await page.Keyboard.PressAsync("1");
        await EsperarAsync(page, $"{PestanaActiva} === 0");
        await page.Keyboard.PressAsync("2");
        await EsperarAsync(page, $"{PestanaActiva} === 1");
        await EsperarAsync(page, $"{FilaConBoton} >= 0");

        // --- j/k recorren las filas; sin fila enfocada, la primera. La fila recibe el foco real. ---
        await Assertions.Expect(enfocada).ToHaveCountAsync(0);
        await page.Keyboard.PressAsync("j");
        await EsperarAsync(page, $"{FilaEnfocada} === 0");
        await Assertions.Expect(enfocada).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("j");
        await EsperarAsync(page, $"{FilaEnfocada} === 1");
        await Assertions.Expect(enfocada).ToHaveCountAsync(1);
        await page.Keyboard.PressAsync("k");
        await EsperarAsync(page, $"{FilaEnfocada} === 0");

        // Hasta la primera fila con botón.
        var indiceConBoton = await page.EvaluateAsync<int>($"() => {FilaConBoton}");
        for (var i = 0; i < indiceConBoton; i++)
            await page.Keyboard.PressAsync("j");
        await EsperarAsync(page, $"{FilaEnfocada} === {indiceConBoton}");

        // --- Alt pulsada y soltada, como en los listados: cifras sobre las pestañas y E sobre
        //     el botón de la fila enfocada. Esc apaga. ---
        await page.Keyboard.PressAsync("Alt");
        await Assertions.Expect(html).ToHaveAttributeAsync("data-keytips", "raiz");
        var pintadas = (await page.Locator(".keytip").AllTextContentsAsync()).Select(l => l.Trim()).ToList();
        foreach (var pista in new[] { "1", "2", "E" })
            Assert.Single(pintadas, l => l == pista);
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.Locator(".keytip")).ToHaveCountAsync(0);

        // --- «e» pulsa el botón de la fila enfocada: abre su diálogo. ---
        await Assertions.Expect(dialogo).ToHaveCountAsync(0);
        await page.Keyboard.PressAsync("e");
        await Assertions.Expect(dialogo).ToBeVisibleAsync();

        // --- Con el diálogo abierto las teclas de ficha no actúan: ni cambia la pestaña ni se
        //     mueve la fila. El control positivo es que antes sí lo hacían. ---
        await page.Keyboard.PressAsync("1");
        await page.Keyboard.PressAsync("k");
        await Assertions.Expect(dialogo).ToBeVisibleAsync();
        Assert.Equal(1, await page.EvaluateAsync<int>($"() => {PestanaActiva}"));
        Assert.Equal(indiceConBoton, await page.EvaluateAsync<int>($"() => {FilaEnfocada}"));
    }

    private const string Filas = "[...document.querySelectorAll(\"[data-atajos-ficha] .cuerpo-con-lateral-principal [data-pieza='fila']\")]";
    private const string PestanaActiva = "[...document.querySelectorAll('[role=tab]')].findIndex(t => t.getAttribute('aria-selected') === 'true')";
    private const string FilaEnfocada = Filas + ".findIndex(f => f.hasAttribute('data-fila-enfocada'))";
    private const string FilaConBoton = Filas + ".findIndex(f => f.querySelector('.fila-relacion-acciones button:not(.boton-360)'))";

    private static Task EsperarAsync(IPage page, string condicion, float tiempoMs = 15_000) =>
        page.WaitForFunctionAsync($"() => {condicion}", null, new() { Timeout = tiempoMs });
}
