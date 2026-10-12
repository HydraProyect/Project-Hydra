using Microsoft.Playwright;

namespace CaeManager.E2ETests;

[Collection("AppCollection")]
public class AcordeonAsignacionesCentroTecladoTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Trabajadores_del_acordeon_entran_en_j_k_y_e_pulsa_la_accion_que_la_fila_ofrece()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailGestorRefrielectric, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/centros");
        await Ayudas.MostrarCentrosSinAgruparAsync(page);
        await page.WaitForSelectorAsync(".lista-filas-acordeon a.boton-360-pagina");
        var rutas = await page.Locator(".lista-filas-acordeon a.boton-360-pagina").EvaluateAllAsync<string[]>(
            "elementos => [...new Set(elementos.map(e => e.getAttribute('href')))]");

        const string selectorFilas = ".lista-trabajadores-acordeon .fila-relacion";
        var filas = page.Locator(selectorFilas);
        var encontrado = false;
        foreach (var ruta in rutas)
        {
            await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}{ruta}?pestana=trabajadores");
            try
            {
                await page.WaitForFunctionAsync("selector => [...document.querySelectorAll(selector)].filter(e => e.getBoundingClientRect().height > 0).length >= 2",
                    selectorFilas, new() { Timeout = 5_000 });
            }
            catch (TimeoutException) { continue; }
            encontrado = true;
            break;
        }
        Assert.True(encontrado, "La siembra debe contener un Centro con dos Trabajadores asignados.");
        // El orden se observa en la lista visible; los controles se localizan por su
        // identidad accesible, sin fijar una posición ni un nombre de la siembra.
        var identidades = await filas.EvaluateAllAsync<string[][]>(
            "elementos => elementos.filter(e => e.getBoundingClientRect().height > 0).map(e => [e.querySelector('.fila-relacion-nombre').textContent.trim(), e.querySelector('.fila-relacion-desplegar').getAttribute('aria-label')])");
        var primero = identidades[0][0];
        var segundo = identidades[1][0];
        var nombre = page.GetByRole(AriaRole.Button, new() { Name = primero, Exact = true });
        var primeraFila = filas.Filter(new() { Has = nombre });
        var desplegar = page.GetByRole(AriaRole.Button, new() { Name = identidades[0][1], Exact = true });
        await desplegar.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(desplegar).ToHaveAttributeAsync("aria-expanded", "true");
        await page.Keyboard.PressAsync("Space");
        await Assertions.Expect(desplegar).ToHaveAttributeAsync("aria-expanded", "false");

        await nombre.FocusAsync();
        await page.Keyboard.PressAsync("j");
        var enfocada = page.Locator("[data-fila-enfocada]");
        await Assertions.Expect(enfocada).ToContainTextAsync(segundo);
        await page.Keyboard.PressAsync("k");
        await Assertions.Expect(enfocada).ToContainTextAsync(primero);
        var estado = await primeraFila.Locator(".fila-relacion-estado").BoundingBoxAsync();
        var pastilla = await primeraFila.Locator(".fila-relacion-estado .badge").BoundingBoxAsync();
        Assert.NotNull(estado);
        Assert.NotNull(pastilla);
        Assert.True(pastilla.Width <= estado.Width + 1, "La pastilla debe caber en la columna de estado.");
        Assert.True(await nombre.EvaluateAsync<bool>("e => e.scrollWidth <= e.clientWidth + 1"), "El nombre completo debe caber sin recorte horizontal.");
        var divisores = await filas.EvaluateAllAsync<float[]>(
            "elementos => elementos.filter(e => e.getBoundingClientRect().height > 0).map(e => parseFloat(getComputedStyle(e).borderTopWidth))");
        Assert.Equal(0, divisores[0]);
        Assert.All(divisores.Skip(1), borde => Assert.Equal(1, borde));
        await Assertions.Expect(page.Locator(".lista-trabajadores-acordeon .lista-relaciones-tarjeta")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator(".lista-trabajadores-acordeon .lista-relaciones")).ToHaveAttributeAsync("aria-label", new System.Text.RegularExpressions.Regex("^Trabajadores · .+$"));
        var conPedir = filas.Filter(new() { Has = page.Locator(".fila-relacion-acciones").GetByRole(AriaRole.Button, new() { Name = "Pedir", Exact = true }) });
        if (await conPedir.CountAsync() > 0)
        {
            var nombreConPedir = await conPedir.EvaluateAllAsync<string>("elementos => elementos.find(e => e.getBoundingClientRect().height > 0).querySelector('.fila-relacion-nombre').textContent.trim()");
            await page.GetByRole(AriaRole.Button, new() { Name = nombreConPedir, Exact = true }).FocusAsync();
            await page.Keyboard.PressAsync("e");
            await Assertions.Expect(page.GetByRole(AriaRole.Dialog, new() { Name = "Reclamar en lote", Exact = true })).ToBeVisibleAsync();
        }
        else
        {
            await page.Keyboard.PressAsync("e");
            await Assertions.Expect(page.Locator(".workspace-panel")).ToHaveCountAsync(0);
        }

    }
}
