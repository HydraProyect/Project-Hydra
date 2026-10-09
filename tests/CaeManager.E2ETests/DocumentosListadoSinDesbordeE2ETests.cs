using System.Text.Json;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Geometría del listado de Documentos: la tabla cabe en su hueco y, cuando la ventana es demasiado
/// estrecha para sus siete columnas, la que se desplaza en horizontal es la propia tabla, nunca la página.
/// En staging (2026-10-08, ventana de 1266 px) la tabla medía 1231 px en un hueco de 927 y lo que sobraba lo
/// desplazaba <c>main.contenido</c>, que es el contenedor de desplazamiento de TODA la página: se iban con
/// ella la cabecera, las pestañas y los filtros. Un test de bUnit no puede verlo: no hay maquetación.
/// <para>
/// <b>Qué mira y qué no.</b> Compara <c>scrollWidth</c> con <c>clientWidth</c> en cada antepasado de la
/// tabla, hasta <c>html</c>; no afirma ningún ancho en píxeles. Mirar solo <c>html</c> o <c>body</c> no
/// bastaría: con el defecto delante, los dos medían lo mismo que la ventana, porque quien absorbía el
/// desborde era <c>main.contenido</c>. Los anchos mínimos dependen de los datos sembrados (palabra más
/// larga de un nombre, insignias de plataforma): con otros datos la tabla puede necesitar más sitio.
/// </para>
/// </summary>
[Collection("AppCollection")]
public class DocumentosListadoSinDesbordeE2ETests(WebAppFixture fixture)
{
    /// <summary>
    /// Devuelve los antepasados de la tabla cuyo contenido no les cabe (los que se desplazarían en
    /// horizontal), cuánto sobresale la tabla de su hueco y cuánto contenido propio le queda por desplazar.
    /// Con <c>reservaBarra</c> estrecha antes el hueco lo que ocupa una barra de desplazamiento vertical
    /// clásica: Chromium sin cabeza las pinta superpuestas, y en el navegador de una persona el hueco es
    /// esos píxeles más estrecho (942 frente a 927 en la medición de staging).
    /// </summary>
    private const string MedirDesborde = """
        reservaBarra => {
            const contenido = document.querySelector('main.contenido');
            contenido.style.paddingRight = '';
            if (reservaBarra > 0)
                contenido.style.paddingRight = `${parseFloat(getComputedStyle(contenido).paddingRight) + reservaBarra}px`;
            const tabla = document.querySelector('table.tabla-datos');
            tabla.scrollLeft = 0;
            const nombre = el => el.tagName.toLowerCase() + (typeof el.className === 'string' && el.className.trim() ? '.' + el.className.trim().split(/\s+/).join('.') : '');
            const desbordados = [];
            for (let el = tabla.parentElement; el; el = el.parentElement)
                if (el.scrollWidth > el.clientWidth + 1)
                    desbordados.push(`${nombre(el)}: contenido de ${el.scrollWidth} px en ${el.clientWidth} px`);
            const filas = Array.from(tabla.querySelectorAll('tbody tr'));
            const hueco = tabla.parentElement.getBoundingClientRect();
            const caja = tabla.getBoundingClientRect();
            const porDesplazar = tabla.scrollWidth - tabla.clientWidth;
            // Con desplazamiento propio, la última columna tiene que poder traerse a la vista.
            tabla.scrollLeft = tabla.scrollWidth;
            const disparador = filas.length ? filas[0].querySelector('.menu-acciones-disparador') : null;
            const accionesFuera = disparador ? disparador.getBoundingClientRect().right - tabla.getBoundingClientRect().right : null;
            tabla.scrollLeft = 0;
            return {
                filas: filas.length,
                filasConAcciones: filas.filter(f => f.querySelector('.menu-acciones-disparador')).length,
                rotulos: Array.from(tabla.querySelectorAll('thead th .col-title-text')).map(t => t.textContent.trim()),
                desbordados,
                sobresale: caja.right - hueco.right,
                porDesplazar,
                desplazamientoPropio: getComputedStyle(tabla).overflowX,
                accionesFuera,
                anchoTabla: Math.round(caja.width),
                anchoHueco: Math.round(hueco.width)
            };
        }
        """;

    /// <summary>
    /// 1024, 1266 y 1440 son los anchos del encargo (1266 es el de la medición de staging). 1240 es el primer
    /// ancho por encima del corte de Documentos.razor.css en el que la tabla deja de desplazarse por su
    /// cuenta: si a ese ancho no cupiera, entre el corte y el ancho en que sí cabe volvería a moverse la página.
    /// </summary>
    [Theory]
    [InlineData(1024, false)]
    [InlineData(1240, true)]
    [InlineData(1266, true)]
    [InlineData(1440, true)]
    public async Task El_listado_no_desplaza_la_pagina_en_horizontal_y_conserva_sus_columnas_y_acciones(int anchoVentana, bool debeCaberSinDesplazarse)
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(anchoVentana, 800);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos");
        await Expect(page.Locator("table.tabla-datos tbody tr .menu-acciones-disparador")).Not.ToHaveCountAsync(0);

        foreach (var reservaBarra in new[] { 0, 17 })
        {
            var medicion = await page.EvaluateAsync<JsonElement>(MedirDesborde, reservaBarra);
            var donde = $"[{anchoVentana} px{(reservaBarra > 0 ? $", con {reservaBarra} px de barra vertical" : string.Empty)}; "
                + $"tabla de {medicion.GetProperty("anchoTabla").GetInt32()} px en un hueco de {medicion.GetProperty("anchoHueco").GetInt32()} px]";

            // Sin filas no habría nada que desbordar y el test pasaría en vacío.
            var filas = medicion.GetProperty("filas").GetInt32();
            Assert.True(filas > 0, $"{donde} el listado sembrado no tiene filas: no hay nada que medir.");

            // No se arregla el desborde quitando columnas ni acciones.
            var rotulos = medicion.GetProperty("rotulos").EnumerateArray().Select(r => r.GetString()!).ToList();
            Assert.Equal(7, rotulos.Count);
            foreach (var esperado in new[] { "Entidad asociada", "Tipo de documento", "Vigencia", "Estado", "Plataformas", "Archivo", "Acciones" })
                Assert.Contains(rotulos, r => r.StartsWith(esperado, StringComparison.Ordinal));
            Assert.Equal(filas, medicion.GetProperty("filasConAcciones").GetInt32());

            var desbordados = medicion.GetProperty("desbordados").EnumerateArray().Select(d => d.GetString()).ToList();
            Assert.True(desbordados.Count == 0,
                $"{donde} el listado desplaza en horizontal a sus antepasados (la página entera):{Environment.NewLine}{string.Join(Environment.NewLine, desbordados)}");
            var sobresale = medicion.GetProperty("sobresale").GetDouble();
            Assert.True(sobresale <= 0.5, $"{donde} la tabla sobresale {sobresale:0.#} px de su hueco.");

            var porDesplazar = medicion.GetProperty("porDesplazar").GetInt32();
            if (debeCaberSinDesplazarse)
            {
                Assert.True(porDesplazar <= 1, $"{donde} la tabla no cabe: le quedan {porDesplazar} px por desplazar.");
            }
            else if (porDesplazar > 1)
            {
                // No cabe: el desplazamiento es de la tabla y la columna de acciones se alcanza con él.
                Assert.Contains(medicion.GetProperty("desplazamientoPropio").GetString()!, new[] { "auto", "scroll" });
                var accionesFuera = medicion.GetProperty("accionesFuera").GetDouble();
                Assert.True(accionesFuera <= 0.5, $"{donde} al desplazar la tabla hasta el final, «Acciones» queda {accionesFuera:0.#} px fuera.");
            }
        }
    }
}
