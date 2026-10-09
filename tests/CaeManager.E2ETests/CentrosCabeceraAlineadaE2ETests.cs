using System.Text.Json;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Geometría del listado de Centros: cada hueco de la cabecera de columnas empieza donde empieza su celda,
/// en todas las filas de la página. La cabecera y cada fila son cajas flexibles DISTINTAS que solo coinciden
/// si sus hijos de ancho fijo miden lo mismo en las dos: con la selección múltiple puesta, la casilla de la
/// fila medía lo que dijera el navegador (20 px en Chromium) y su hueco de la cabecera 16, así que «Centro»
/// y «Empresa» no caían sobre sus celdas. Un test de bUnit no puede verlo: no hay maquetación.
/// <para>
/// <b>Qué mira y qué no.</b> Compara el borde izquierdo de cada hueco de la cabecera con el de la celda que
/// ocupa su mismo lugar en la fila, sin afirmar ningún ancho en píxeles. Los tres indicadores («Venc.»,
/// «Próx.», «Estado / visita») se comparan uno a uno, no como bloque. No mira dónde empieza el contenido
/// DENTRO de cada celda.
/// </para>
/// </summary>
[Collection("AppCollection")]
public class CentrosCabeceraAlineadaE2ETests(WebAppFixture fixture)
{
    /// <summary>Medio píxel de tolerancia de redondeo: el desvío del defecto medía 4 px.</summary>
    private const string MedirDesalineacion = """
        () => {
            const indicadores = '.cabecera-columnas-centros-indicadores, .tarjeta-fila-acordeon-indicadores';
            const huecos = el => Array.from(el.children).flatMap(h => h.matches(indicadores) ? Array.from(h.children) : [h]);
            const cabecera = huecos(document.querySelector('.cabecera-columnas-centros'));
            const filas = Array.from(document.querySelectorAll('.lista-filas-acordeon .tarjeta-fila-acordeon-cabecera'));
            const desvios = [];
            filas.forEach((fila, i) => {
                const celdas = huecos(fila);
                if (celdas.length !== cabecera.length) {
                    desvios.push(`fila ${i}: ${celdas.length} celdas frente a ${cabecera.length} huecos de la cabecera`);
                    return;
                }
                cabecera.forEach((hueco, j) => {
                    const d = celdas[j].getBoundingClientRect().left - hueco.getBoundingClientRect().left;
                    if (Math.abs(d) > 0.5)
                        desvios.push(`fila ${i}: la celda ${j + 1} («${hueco.textContent.trim() || 'sin rótulo'}») empieza ${d.toFixed(1)} px a la derecha de su hueco de la cabecera`);
                });
            });
            return {
                filas: filas.length,
                columnas: cabecera.length,
                rotulos: cabecera.filter(h => h.textContent.trim()).length,
                desvios
            };
        }
        """;

    [Fact]
    public async Task Cada_hueco_de_la_cabecera_empieza_donde_empieza_su_celda_en_claro_y_oscuro()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirCentrosAsync(contexto);
        // Los grupos por Cliente empresarial arrancan contraídos: sin agrupar se ven todas las filas.
        await Ayudas.MostrarCentrosSinAgruparAsync(page);

        // chevron · Centro · Empresa · Cumplimiento · Venc. · Próx. · Estado / visita · acciones
        await AfirmarAlineadoEnLosDosTemasAsync(page, columnas: 8, "sin selección");
    }

    [Fact]
    public async Task Con_la_seleccion_multiple_la_casilla_no_descoloca_las_columnas_en_claro_y_oscuro()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirCentrosAsync(contexto);

        // El conmutador llega con el prerender antes que el circuito y un clic en esa ventana se pierde: se
        // repite hasta que el propio botón dice que está pulsado (como Ayudas.MostrarCentrosSinAgruparAsync).
        // Con la selección puesta los grupos se muestran abiertos, así que las filas se ven sin desagrupar.
        var conmutador = page.Locator("header.cabecera-pagina button.cabecera-listado-icono[aria-label='Selección múltiple']");
        await conmutador.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        for (var intento = 1; await conmutador.GetAttributeAsync("aria-pressed") != "true"; intento++)
        {
            Assert.True(intento <= 10, "«Selección múltiple» no se aplicó tras 10 clics.");
            await conmutador.ClickAsync();
            await page.WaitForTimeoutAsync(1_000);
        }
        await Expect(page.Locator(".cabecera-columnas-centros-seleccion")).ToBeVisibleAsync();

        // casilla · chevron · Centro · Empresa · Cumplimiento · Venc. · Próx. · Estado / visita · acciones
        await AfirmarAlineadoEnLosDosTemasAsync(page, columnas: 9, "con selección múltiple");
    }

    private async Task<IPage> AbrirCentrosAsync(IBrowserContext contexto)
    {
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(1266, 800);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/centros");
        await page.Locator(".cabecera-columnas-centros").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        return page;
    }

    private static async Task AfirmarAlineadoEnLosDosTemasAsync(IPage page, int columnas, string modo)
    {
        await Expect(page.Locator(".lista-filas-acordeon .tarjeta-fila-acordeon-cabecera")).Not.ToHaveCountAsync(0);
        foreach (var tema in new[] { "claro", "oscuro" })
        {
            await page.EvaluateAsync("tema => document.documentElement.setAttribute('data-theme', tema)", tema);
            var medicion = await page.EvaluateAsync<JsonElement>(MedirDesalineacion);
            var contexto = $"tema {tema}, {modo}";

            // Sin filas, sin rótulos o con otra cabecera no habría nada que comparar y el test pasaría en vacío.
            Assert.True(medicion.GetProperty("filas").GetInt32() > 0, $"[{contexto}] la lista sembrada no tiene filas a la vista: no hay nada que medir.");
            Assert.Equal(columnas, medicion.GetProperty("columnas").GetInt32());
            Assert.Equal(6, medicion.GetProperty("rotulos").GetInt32());

            var desvios = medicion.GetProperty("desvios").EnumerateArray().Select(d => d.GetString()).ToList();
            Assert.True(desvios.Count == 0, $"[{contexto}] la cabecera no coincide con sus celdas:{Environment.NewLine}{string.Join(Environment.NewLine, desvios.Take(12))}");
        }
    }
}
