using System.Text.Json;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Geometría del listado de Documentos: cuando la tabla no cabe en su hueco, lo que se desplaza en
/// horizontal es su envoltorio (<c>.documentos-tabla-desplazable</c>), nunca la página. Antes lo que sobraba
/// lo desplazaba <c>main.contenido</c>, que es el contenedor de desplazamiento de TODA la página: se iban con
/// la tabla la cabecera, las pestañas y los filtros. Un test de bUnit no puede verlo: no hay maquetación.
/// <para>
/// <b>Qué mira y qué no.</b> Compara <c>scrollWidth</c> con <c>clientWidth</c> en cada antepasado del
/// envoltorio, hasta <c>html</c>; no afirma ningún ancho en píxeles, porque el mínimo de la tabla depende de
/// los datos sembrados. Mirar solo <c>html</c> o <c>body</c> no bastaría: con el defecto delante los dos
/// miden lo mismo que la ventana. No mira la cabecera fija de la tabla, que con el envoltorio deja de
/// pegarse al bajar por la lista (precio aceptado de la decisión del 2026-10-09).
/// </para>
/// </summary>
[Collection("AppCollection")]
public class DocumentosTablaDesplazableE2ETests(WebAppFixture fixture, ITestOutputHelper salida)
{
    /// <summary>
    /// Con <c>reservaBarra</c> estrecha antes el hueco lo que ocupa una barra de desplazamiento vertical
    /// clásica: Chromium sin cabeza las pinta superpuestas, y en el navegador de una persona el hueco es
    /// esos píxeles más estrecho.
    /// </summary>
    private const string MedirDesborde = """
        reservaBarra => {
            const contenido = document.querySelector('main.contenido');
            contenido.style.paddingRight = '';
            if (reservaBarra > 0)
                contenido.style.paddingRight = `${parseFloat(getComputedStyle(contenido).paddingRight) + reservaBarra}px`;
            const envoltorio = document.querySelector('.documentos-tabla-desplazable');
            const tabla = envoltorio.querySelector('table.tabla-datos');
            envoltorio.scrollLeft = 0;
            const nombre = el => el.tagName.toLowerCase() + (typeof el.className === 'string' && el.className.trim() ? '.' + el.className.trim().split(/\s+/).join('.') : '');
            const antepasadosDesbordados = [];
            for (let el = envoltorio.parentElement; el; el = el.parentElement)
                if (el.scrollWidth > el.clientWidth + 1)
                    antepasadosDesbordados.push(`${nombre(el)}: contenido de ${el.scrollWidth} px en ${el.clientWidth} px`);
            // Con desplazamiento propio, la última columna tiene que poder traerse a la vista.
            envoltorio.scrollLeft = envoltorio.scrollWidth;
            const cabeceras = Array.from(tabla.querySelectorAll('thead th'));
            const ultimaFuera = cabeceras[cabeceras.length - 1].getBoundingClientRect().right - envoltorio.getBoundingClientRect().right;
            envoltorio.scrollLeft = 0;
            return {
                filas: tabla.querySelectorAll('tbody tr').length,
                columnas: cabeceras.length,
                antepasadosDesbordados,
                porDesplazar: envoltorio.scrollWidth - envoltorio.clientWidth,
                ultimaFuera,
                anchoTabla: Math.round(tabla.getBoundingClientRect().width),
                anchoHueco: envoltorio.clientWidth
            };
        }
        """;

    /// <summary>
    /// 1440, 1280 y 768 son los anchos del encargo; 768 es el primero en el que manda el envoltorio (de 767
    /// para abajo la propia tabla es el contenedor). Con «Selección múltiple» la tabla tiene una columna más.
    /// </summary>
    [Theory]
    [InlineData(768, false)]
    [InlineData(1280, false)]
    [InlineData(1440, false)]
    [InlineData(1280, true)]
    public async Task La_tabla_desplaza_dentro_de_su_envoltorio_y_la_pagina_no_se_mueve_en_horizontal(int anchoVentana, bool seleccionMultiple)
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirDocumentosAsync(contexto, anchoVentana);
        if (seleccionMultiple)
        {
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Selección múltiple", Exact = true }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Checkbox, new PageGetByRoleOptions { Name = "Seleccionar los documentos de esta página", Exact = true }))
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        }

        foreach (var reservaBarra in new[] { 0, 17 })
        {
            var medicion = await page.EvaluateAsync<JsonElement>(MedirDesborde, reservaBarra);
            var porDesplazar = medicion.GetProperty("porDesplazar").GetInt32();
            var donde = $"[{anchoVentana} px{(seleccionMultiple ? ", selección múltiple" : string.Empty)}{(reservaBarra > 0 ? $", {reservaBarra} px de barra vertical" : string.Empty)}; "
                + $"tabla de {medicion.GetProperty("anchoTabla").GetInt32()} px en un hueco de {medicion.GetProperty("anchoHueco").GetInt32()} px]";
            salida.WriteLine($"MEDIDA {donde} desplazamiento propio del envoltorio: {porDesplazar} px");

            // Sin filas no habría nada que desbordar y el test pasaría en vacío.
            Assert.True(medicion.GetProperty("filas").GetInt32() > 0, $"{donde} el listado sembrado no tiene filas: no hay nada que medir.");

            // No se arregla el desborde quitando columnas.
            Assert.Equal(seleccionMultiple ? 8 : 7, medicion.GetProperty("columnas").GetInt32());

            var desbordados = medicion.GetProperty("antepasadosDesbordados").EnumerateArray().Select(d => d.GetString()).ToList();
            Assert.True(desbordados.Count == 0,
                $"{donde} el listado desplaza en horizontal a sus antepasados (la página entera):{Environment.NewLine}{string.Join(Environment.NewLine, desbordados)}");

            if (porDesplazar > 0)
            {
                var ultimaFuera = medicion.GetProperty("ultimaFuera").GetDouble();
                Assert.True(ultimaFuera <= 0.5, $"{donde} al desplazar el envoltorio hasta el final, la última columna queda {ultimaFuera:0.#} px fuera.");
            }
        }
    }

    /// <summary>
    /// A 1024 px el hueco mide unos 700 y la tabla no cabe con ninguna siembra razonable: es el ancho en el
    /// que el desplazamiento existe seguro. Aquí se prueba que se alcanza con el teclado y, de paso, que el
    /// instrumento del test de arriba ve el defecto: sin el desplazamiento del envoltorio, quien desborda es
    /// un antepasado.
    /// </summary>
    [Fact]
    public async Task El_desplazamiento_horizontal_se_alcanza_con_el_teclado_y_sin_el_envoltorio_se_mueve_la_pagina()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirDocumentosAsync(contexto, 1024);
        var envoltorio = page.GetByRole(AriaRole.Region, new PageGetByRoleOptions { Name = "Listado de documentos, desplazable en horizontal", Exact = true });

        var medicion = await page.EvaluateAsync<JsonElement>(MedirDesborde, 0);
        Assert.True(medicion.GetProperty("porDesplazar").GetInt32() > 0, "A 1024 px la tabla cabe en su hueco: este test no tiene nada que desplazar.");
        Assert.Equal(0, medicion.GetProperty("antepasadosDesbordados").GetArrayLength());

        await Expect(envoltorio).ToHaveAttributeAsync("tabindex", "0");
        await envoltorio.FocusAsync();
        await Expect(envoltorio).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("ArrowRight");
        await page.WaitForFunctionAsync("() => document.querySelector('.documentos-tabla-desplazable').scrollLeft > 0");
        Assert.Equal(0, await page.EvaluateAsync<int>("() => Math.round(document.querySelector('main.contenido').scrollLeft)"));

        // Control positivo del instrumento: quitado el desplazamiento propio, la medida señala a un antepasado.
        await page.EvaluateAsync("() => { document.querySelector('.documentos-tabla-desplazable').style.overflowX = 'visible'; }");
        var sinEnvoltorio = await page.EvaluateAsync<JsonElement>(MedirDesborde, 0);
        Assert.True(sinEnvoltorio.GetProperty("antepasadosDesbordados").GetArrayLength() > 0,
            "Sin el desplazamiento del envoltorio ningún antepasado desborda: la medida no puede ver el defecto.");
    }

    private async Task<IPage> AbrirDocumentosAsync(IBrowserContext contexto, int anchoVentana)
    {
        var page = await contexto.NewPageAsync();
        // La sesión se inicia a ancho de escritorio: IniciarSesionAsync espera la navegación principal, que a
        // 768 px está plegada. El ancho que se mide se fija antes de abrir el listado.
        await page.SetViewportSizeAsync(1280, 800);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await page.SetViewportSizeAsync(anchoVentana, 800);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos");
        await Expect(page.Locator(".documentos-tabla-desplazable table.tabla-datos tbody tr")).Not.ToHaveCountAsync(0);
        return page;
    }
}
