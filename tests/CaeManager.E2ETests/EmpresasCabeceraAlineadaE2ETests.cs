using System.Text.Json;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Geometría del listado de Empresas: cada rótulo de la cabecera de columnas empieza donde empieza su celda,
/// en todas las filas de la página. La cabecera y cada fila son rejillas CSS distintas que comparten
/// definición (<c>.rejilla-empresas</c>): si alguna pista se resuelve por contenido, cada rejilla la mide por
/// su cuenta y las columnas dejan de coincidir (en staging, con main d77cd20b, «Cumplimiento», «Documentación»
/// y «Detecciones» se veían desplazadas). Un test de bUnit no puede verlo: no hay maquetación.
/// Se mide con los datos sembrados, a 1280 px, en tema claro y oscuro y con la selección múltiple puesta.
/// </summary>
[Collection("AppCollection")]
public class EmpresasCabeceraAlineadaE2ETests(WebAppFixture fixture)
{
    /// <summary>
    /// Dos medidas por fila, con medio píxel de tolerancia de redondeo (un desplazamiento real mide decenas):
    /// las pistas que resuelve su rejilla frente a las de la cabecera, y el borde izquierdo de cada rótulo
    /// frente al de su celda. Las celdas sin rótulo (chevron, casilla, acciones) no se comparan por borde: su
    /// contenido no tiene por qué empezar donde empieza la pista.
    /// </summary>
    private const string MedirDesalineacion = """
        () => {
            const pistas = el => getComputedStyle(el).gridTemplateColumns.split(' ')
                .map(p => Math.round(parseFloat(p) * 2) / 2).join(' ');
            const cabecera = document.querySelector('.cabecera-columnas-empresas');
            const pistasCabecera = pistas(cabecera);
            const celdasCabecera = Array.from(cabecera.children);
            const filas = Array.from(document.querySelectorAll('.marco-lista-empresas .tarjeta-fila-acordeon-cabecera'));
            const desvios = [];
            filas.forEach((fila, i) => {
                const pistasFila = pistas(fila);
                if (pistasFila !== pistasCabecera)
                    desvios.push(`fila ${i}: pistas [${pistasFila}] distintas de las de la cabecera [${pistasCabecera}]`);
                const celdas = Array.from(fila.children);
                if (celdas.length !== celdasCabecera.length) {
                    desvios.push(`fila ${i}: ${celdas.length} celdas frente a ${celdasCabecera.length} de la cabecera`);
                    return;
                }
                celdasCabecera.forEach((rotulo, j) => {
                    const texto = rotulo.textContent.trim();
                    if (!texto) return;
                    const d = rotulo.getBoundingClientRect().left - celdas[j].getBoundingClientRect().left;
                    if (Math.abs(d) > 0.5)
                        desvios.push(`fila ${i}: «${texto}» empieza ${d.toFixed(1)} px a la derecha de su celda`);
                });
            });
            return {
                filas: filas.length,
                columnas: celdasCabecera.length,
                rotulos: celdasCabecera.filter(c => c.textContent.trim()).length,
                desvios
            };
        }
        """;

    [Fact]
    public async Task Cada_rotulo_de_la_cabecera_empieza_donde_empieza_su_celda_en_claro_oscuro_y_con_seleccion()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 800);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/empresas");
        await Expect(page.Locator(".marco-lista-empresas .tarjeta-fila-acordeon-cabecera")).Not.ToHaveCountAsync(0);

        foreach (var tema in new[] { "claro", "oscuro" })
        {
            await page.EvaluateAsync("tema => document.documentElement.setAttribute('data-theme', tema)", tema);
            await AfirmarAlineadoAsync(page, columnas: 6, $"tema {tema}");
        }

        // El clic se repite hasta que el propio conmutador dice que está pulsado, porque llega con el
        // prerender antes que el circuito (mismo patrón que Ayudas.MostrarCentrosSinAgruparAsync).
        var conmutador = page.Locator("header.cabecera-pagina button.cabecera-listado-icono[aria-label='Selección múltiple']");
        for (var intento = 1; await conmutador.GetAttributeAsync("aria-pressed") != "true"; intento++)
        {
            Assert.True(intento <= 10, "«Selección múltiple» no se aplicó tras 10 clics.");
            await conmutador.ClickAsync();
            await page.WaitForTimeoutAsync(1_000);
        }

        await Expect(page.Locator(".cabecera-columnas-empresas.rejilla-empresas-seleccion")).ToBeVisibleAsync();
        await AfirmarAlineadoAsync(page, columnas: 7, "tema oscuro con selección múltiple");
    }

    private static async Task AfirmarAlineadoAsync(IPage page, int columnas, string contexto)
    {
        var medicion = await page.EvaluateAsync<JsonElement>(MedirDesalineacion);

        // Sin filas, sin rótulos o con otra cabecera no habría nada que comparar y el test pasaría en vacío.
        Assert.True(medicion.GetProperty("filas").GetInt32() > 0, $"[{contexto}] la lista sembrada no tiene filas: no hay nada que medir.");
        Assert.Equal(columnas, medicion.GetProperty("columnas").GetInt32());
        Assert.Equal(4, medicion.GetProperty("rotulos").GetInt32());

        var desvios = medicion.GetProperty("desvios").EnumerateArray().Select(d => d.GetString()).ToList();
        Assert.True(desvios.Count == 0, $"[{contexto}] la cabecera no coincide con sus celdas:{Environment.NewLine}{string.Join(Environment.NewLine, desvios)}");
    }
}
