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
/// <b>Qué mira y qué no.</b> Los dos casos de alineación comparan el borde izquierdo de cada hueco de la cabecera con el de la celda que
/// ocupa su mismo lugar en la fila, sin afirmar ningún ancho en píxeles. La Visita no es columna (cierra la
/// segunda línea del Centro) y «Estado» es una sola columna. No mira dónde empieza el contenido
/// DENTRO de cada celda.
/// </para>
/// </summary>
[Collection("AppCollectionListados")]
public class CentrosCabeceraAlineadaE2ETests(WebAppFixtureListados fixture)
{
    /// <summary>Medio píxel de tolerancia de redondeo: el desvío del defecto medía 4 px.</summary>
    private const string MedirDesalineacion = """
        () => {
            const huecos = el => Array.from(el.children);
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
        // De serie la lista no se agrupa: se asegura por si la vista recordada trajera «Por Cliente».
        await Ayudas.MostrarCentrosSinAgruparAsync(page);

        // chevron · Centro · Empresa · Cumplimiento · Estado · acciones
        await AfirmarAlineadoEnLosDosTemasAsync(page, columnas: 6, "sin selección");
    }

    [Fact]
    public async Task Con_la_seleccion_multiple_la_casilla_no_descoloca_las_columnas_en_claro_y_oscuro()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirCentrosAsync(contexto);

        // El conmutador llega con el prerender antes que el circuito y un clic en esa ventana se pierde: se
        // repite, una vez por segundo, hasta que el propio botón dice que está pulsado (aria-pressed).
        await page.WaitForFunctionAsync("""
            () => {
                const conmutador = document.querySelector("button[aria-label='Selección múltiple']");
                if (!conmutador) return false;
                if (conmutador.getAttribute('aria-pressed') === 'true') return true;
                conmutador.click();
                return false;
            }
            """, null, new PageWaitForFunctionOptions { PollingInterval = 1_000, Timeout = 30_000 });
        // El hueco de la casilla en la cabecera está vacío y no tiene alto: se afirma que existe, no que se ve.
        await Expect(page.Locator(".cabecera-columnas-centros .seleccion-centro")).ToHaveCountAsync(1);

        // casilla · chevron · Centro · Empresa · Cumplimiento · Estado · acciones
        await AfirmarAlineadoEnLosDosTemasAsync(page, columnas: 7, "con selección múltiple");
    }

    [Fact]
    public async Task La_variante_visual_del_listado_mide_titulo_cabecera_y_accion_compacta_en_ambos_temas()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirCentrosAsync(contexto);
        await page.SetViewportSizeAsync(1526, 1200);
        await Ayudas.MostrarCentrosSinAgruparAsync(page);
        foreach (var boton in await page.GetByRole(AriaRole.Button,
            new() { NameRegex = new System.Text.RegularExpressions.Regex("^Asignaciones de ") }).AllAsync())
        {
            if (await boton.GetAttributeAsync("aria-expanded") != "true") await boton.ClickAsync();
            if (await page.Locator(".fila-trabajador-gestionar").CountAsync() > 0) break;
        }
        await Expect(page.Locator(".fila-trabajador-gestionar")).Not.ToHaveCountAsync(0);
        foreach (var tema in new[] { "claro", "oscuro" })
        {
            await page.EvaluateAsync("tema => document.documentElement.setAttribute('data-theme', tema)", tema);
            var medida = await page.EvaluateAsync<JsonElement>("""
                () => {
                    const titulo = document.querySelector('main .titulo-pagina');
                    const cabecera = document.querySelector('.cabecera-columnas-centros');
                    const acciones = Array.from(document.querySelectorAll('.fila-trabajador-gestionar'));
                    const cajas = Array.from(document.querySelectorAll('.lista-trabajadores-acordeon-listado .lista-relaciones-tarjeta'));
                    return {
                        fuente: parseFloat(getComputedStyle(titulo).fontSize),
                        cabecera: cabecera.getBoundingClientRect().height,
                        iconos: Array.from(document.querySelectorAll(".acciones-cabecera .cabecera-listado-icono, .acciones-cabecera .menu-acciones-disparador")).map(e => e.getBoundingClientRect().width),
                        primaria: document.querySelector(".acciones-cabecera .boton").getBoundingClientRect().height,
                        espacioRotulos: parseFloat(getComputedStyle(cabecera).letterSpacing),
                        acciones: acciones.map(e => ({ h: e.getBoundingClientRect().height,
                            borde: parseFloat(getComputedStyle(e).borderTopWidth) })),
                        nombres: Array.from(document.querySelectorAll(".lista-trabajadores-acordeon-listado .fila-relacion-nombre")).map(e => parseFloat(getComputedStyle(e).fontSize)),
                        cajas: cajas.map(e => ({ borde: parseFloat(getComputedStyle(e).borderTopWidth),
                            radio: parseFloat(getComputedStyle(e).borderTopLeftRadius) }))
                    };
                }
                """);
            Assert.Equal(22, medida.GetProperty("fuente").GetDouble());
            // Cabecera de columnas de la maqueta: 14 px arriba, 16 de línea, 10 abajo y 1,5 de filete.
            Assert.InRange(medida.GetProperty("cabecera").GetDouble(), 41, 42);
            // La cabecera de la página lleva cuatro controles de icono o menú (selección múltiple, exportar, atajos
            // y «⋯»), y ninguno es más estrecho que el icono compacto de 34 px.
            Assert.Equal(4, medida.GetProperty("iconos").GetArrayLength());
            foreach (var icono in medida.GetProperty("iconos").EnumerateArray())
                Assert.True(icono.GetDouble() >= 34, $"ancho {icono.GetDouble()}");
            Assert.InRange(medida.GetProperty("primaria").GetDouble(), 33.5, 34.5);
            Assert.InRange(medida.GetProperty("espacioRotulos").GetDouble(), 0.9, 1);
            foreach (var accion in medida.GetProperty("acciones").EnumerateArray())
            {
                Assert.InRange(accion.GetProperty("h").GetDouble(), 16, 20);
                Assert.Equal(0, accion.GetProperty("borde").GetDouble());
            }
            foreach (var nombre in medida.GetProperty("nombres").EnumerateArray())
                Assert.Equal(12, nombre.GetDouble());
            foreach (var caja in medida.GetProperty("cajas").EnumerateArray())
            {
                Assert.Equal(0, caja.GetProperty("borde").GetDouble());
                Assert.Equal(0, caja.GetProperty("radio").GetDouble());
            }
            await page.EvaluateAsync("() => window.scrollTo(0, 0)");
            var carpeta = Path.Combine(Path.GetTempPath(), "centros-fidelidad-local");
            Directory.CreateDirectory(carpeta);
            await page.ScreenshotAsync(new() { Path = Path.Combine(carpeta, $"centros-{tema}.png"), FullPage = true });
        }
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
            Assert.Equal(4, medicion.GetProperty("rotulos").GetInt32());

            var desvios = medicion.GetProperty("desvios").EnumerateArray().Select(d => d.GetString()).ToList();
            Assert.True(desvios.Count == 0, $"[{contexto}] la cabecera no coincide con sus celdas:{Environment.NewLine}{string.Join(Environment.NewLine, desvios.Take(12))}");
        }
    }
}
