using System.Text.Json;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Geometría del listado de Documentos: la tabla cabe en su hueco y, cuando la ventana es demasiado
/// estrecha para sus columnas, lo que se desplaza en horizontal es el envoltorio del listado, nunca la página.
/// En staging (2026-10-08, ventana de 1266 px) la tabla medía 1231 px en un hueco de 927 y lo que sobraba lo
/// desplazaba <c>main.contenido</c>, que es el contenedor de desplazamiento de TODA la página: se iban con
/// ella la cabecera, las pestañas y los filtros. Un test de bUnit no puede verlo: no hay maquetación.
/// <para>
/// <b>Qué mira y qué no.</b> Compara <c>scrollWidth</c> con <c>clientWidth</c> en cada antepasado de la
/// tabla, hasta <c>html</c>; no afirma ningún ancho en píxeles. Mirar solo <c>html</c> o <c>body</c> no
/// bastaría: con el defecto delante, los dos medían lo mismo que la ventana, porque quien absorbía el
/// desborde era <c>main.contenido</c>. Los anchos mínimos dependen de los datos sembrados (palabra más
/// larga de un nombre, insignias de plataforma): con otros datos la tabla puede necesitar más sitio. Las
/// medidas de cada caso se escriben en la salida del test, también cuando pasa.
/// </para>
/// </summary>
[Collection("AppCollection")]
public class DocumentosListadoSinDesbordeE2ETests(WebAppFixture fixture, ITestOutputHelper salida)
{
    /// <summary>
    /// Devuelve los antepasados de la tabla cuyo contenido no les cabe (los que se desplazarían en
    /// horizontal) y, aparte, si quien se desplaza es el propio listado (su envoltorio o la tabla), que es
    /// el único al que se le permite. Con <c>reservaBarra</c> estrecha antes el hueco lo que ocupa una barra de
    /// desplazamiento vertical clásica: Chromium sin cabeza las pinta superpuestas, y en el navegador de
    /// una persona el hueco es esos píxeles más estrecho (942 frente a 927 en la medición de staging).
    /// </summary>
    private const string MedirDesborde = """
        reservaBarra => {
            const contenido = document.querySelector('main.contenido');
            contenido.style.paddingRight = '';
            if (reservaBarra > 0)
                contenido.style.paddingRight = `${parseFloat(getComputedStyle(contenido).paddingRight) + reservaBarra}px`;
            const tabla = document.querySelector('table.tabla-datos');
            const hueco = tabla.parentElement;
            hueco.scrollLeft = 0;
            const nombre = el => el.tagName.toLowerCase() + (typeof el.className === 'string' && el.className.trim() ? '.' + el.className.trim().split(/\s+/).join('.') : '');
            const desplaza = el => ['auto', 'scroll'].includes(getComputedStyle(el).overflowX);
            const desbordados = [];
            let porDesplazar = 0;
            for (let el = tabla; el; el = el.parentElement) {
                if (el.scrollWidth <= el.clientWidth + 1) continue;
                if ((el === hueco || el === tabla) && desplaza(el)) porDesplazar = el.scrollWidth - el.clientWidth;
                else desbordados.push(`${nombre(el)}: contenido de ${el.scrollWidth} px en ${el.clientWidth} px`);
            }
            const filas = Array.from(tabla.querySelectorAll('tbody tr'));
            // Con desplazamiento propio, la última columna tiene que poder traerse a la vista.
            hueco.scrollLeft = hueco.scrollWidth;
            tabla.scrollLeft = tabla.scrollWidth;
            const disparador = filas.length ? filas[0].querySelector('.menu-acciones-disparador') : null;
            const accionesFuera = disparador ? disparador.getBoundingClientRect().right - hueco.getBoundingClientRect().right : null;
            hueco.scrollLeft = 0;
            tabla.scrollLeft = 0;
            const ordenables = Array.from(tabla.querySelectorAll('thead button.col-title')).map(b => b.getBoundingClientRect());
            return {
                filas: filas.length,
                filasConAcciones: filas.filter(f => f.querySelector('.menu-acciones-disparador')).length,
                columnas: tabla.querySelectorAll('thead th').length,
                rotulos: Array.from(tabla.querySelectorAll('thead th .col-title-text')).map(t => t.textContent.trim()),
                desbordados,
                porDesplazar,
                accionesFuera,
                anchoTabla: Math.round(tabla.getBoundingClientRect().width),
                anchoHueco: hueco.clientWidth,
                ordenableMenor: `${Math.round(Math.min(...ordenables.map(r => r.width)))}×${Math.round(Math.min(...ordenables.map(r => r.height)))}`
            };
        }
        """;

    /// <summary>
    /// Con un menú «⋯» abierto: cuánto desplazamiento VERTICAL propio le ha aparecido al listado (un
    /// contenedor que desplaza en horizontal también recorta en vertical, y el panel se abre hacia abajo),
    /// cuánto sobresale el panel del envoltorio y qué opciones quedan tapadas una vez traído el panel a la vista.
    /// </summary>
    private const string MedirMenuAbierto = """
        () => {
            const tabla = document.querySelector('table.tabla-datos');
            const hueco = tabla.parentElement;
            const panel = hueco.querySelector('.menu-acciones-panel');
            const vertical = Math.max(hueco.scrollHeight - hueco.clientHeight, tabla.scrollHeight - tabla.clientHeight);
            const sobresale = panel.getBoundingClientRect().bottom - hueco.getBoundingClientRect().bottom;
            panel.scrollIntoView({ block: 'nearest', inline: 'nearest' });
            const opciones = Array.from(panel.querySelectorAll('[role=menuitem]'));
            const tapadas = opciones.filter(o => {
                const r = o.getBoundingClientRect();
                const encima = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
                return !(encima && (encima === o || o.contains(encima)));
            }).map(o => o.textContent.trim());
            return {
                filas: tabla.querySelectorAll('tbody tr').length,
                opciones: opciones.length,
                tapadas,
                vertical,
                sobresale,
                altoPanel: Math.round(panel.getBoundingClientRect().height),
                desplazaEnHorizontal: [hueco, tabla].some(e => ['auto', 'scroll'].includes(getComputedStyle(e).overflowX))
            };
        }
        """;

    /// <summary>
    /// 1024, 1266 y 1440 son los anchos del encargo (1266 es el de la medición de staging). 1240 es el primer
    /// ancho por encima del corte de Documentos.razor.css en el que el listado deja de desplazarse por su
    /// cuenta: si a ese ancho no cupiera, entre el corte y el ancho en que sí cabe volvería a moverse la
    /// página. Con «Selección múltiple» la tabla tiene una columna más (la casilla) y necesita más sitio.
    /// </summary>
    [Theory]
    [InlineData(1024, false, false)]
    [InlineData(1240, false, true)]
    [InlineData(1266, false, true)]
    [InlineData(1440, false, true)]
    [InlineData(1024, true, false)]
    [InlineData(1240, true, true)]
    [InlineData(1266, true, true)]
    [InlineData(1440, true, true)]
    public async Task El_listado_no_desplaza_la_pagina_en_horizontal_y_conserva_sus_columnas_y_acciones(int anchoVentana, bool seleccionMultiple, bool debeCaber)
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirDocumentosAsync(contexto, anchoVentana, "/documentos");
        if (seleccionMultiple)
        {
            // Rol y nombre accesible, como FlujoCicloDocumentalTests; la casilla de la cabecera dice que entró.
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Selección múltiple", Exact = true }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Checkbox, new PageGetByRoleOptions { Name = "Seleccionar los documentos de esta página", Exact = true }))
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        }

        foreach (var reservaBarra in new[] { 0, 17 })
        {
            var medicion = await page.EvaluateAsync<JsonElement>(MedirDesborde, reservaBarra);
            var porDesplazar = medicion.GetProperty("porDesplazar").GetInt32();
            var donde = $"[{anchoVentana} px{(seleccionMultiple ? ", selección múltiple" : string.Empty)}{(reservaBarra > 0 ? $", {reservaBarra} px de barra vertical" : string.Empty)}; "
                + $"tabla de {medicion.GetProperty("anchoTabla").GetInt32()} px en un hueco de {medicion.GetProperty("anchoHueco").GetInt32()} px; "
                + $"cabecera ordenable más pequeña {medicion.GetProperty("ordenableMenor").GetString()} px]";
            salida.WriteLine($"MEDIDA {donde} desplazamiento propio del listado: {porDesplazar} px");

            // Sin filas no habría nada que desbordar y el test pasaría en vacío.
            var filas = medicion.GetProperty("filas").GetInt32();
            Assert.True(filas > 0, $"{donde} el listado sembrado no tiene filas: no hay nada que medir.");

            // No se arregla el desborde quitando columnas ni acciones.
            var rotulos = medicion.GetProperty("rotulos").EnumerateArray().Select(r => r.GetString()!).ToList();
            Assert.Equal(7, rotulos.Count);
            foreach (var esperado in new[] { "Entidad asociada", "Tipo de documento", "Vigencia", "Estado", "Plataformas", "Archivo", "Acciones" })
                Assert.Contains(rotulos, r => r.StartsWith(esperado, StringComparison.Ordinal));
            Assert.Equal(seleccionMultiple ? 8 : 7, medicion.GetProperty("columnas").GetInt32());
            Assert.Equal(filas, medicion.GetProperty("filasConAcciones").GetInt32());

            var desbordados = medicion.GetProperty("desbordados").EnumerateArray().Select(d => d.GetString()).ToList();
            Assert.True(desbordados.Count == 0,
                $"{donde} el listado desplaza en horizontal a sus antepasados (la página entera):{Environment.NewLine}{string.Join(Environment.NewLine, desbordados)}");

            if (debeCaber)
            {
                // Por encima del corte el envoltorio no desplaza y un desborde ya habría caído en la lista de arriba:
                // esto solo se pone rojo si alguien sube el corte por encima de este ancho sin que la tabla quepa.
                Assert.True(porDesplazar == 0, $"{donde} la tabla no cabe: a su envoltorio le quedan {porDesplazar} px por desplazar.");
            }
            else if (porDesplazar > 0)
            {
                // No cabe: el desplazamiento es del envoltorio y la columna de acciones se alcanza con él.
                var accionesFuera = medicion.GetProperty("accionesFuera").GetDouble();
                Assert.True(accionesFuera <= 0.5, $"{donde} al desplazar el listado hasta el final, «Acciones» queda {accionesFuera:0.#} px fuera.");
            }
        }
    }

    /// <summary>
    /// 1100 px, pocas filas y el menú de la ÚLTIMA: es el caso en que el panel no tiene nada debajo salvo el
    /// borde del contenedor que desplaza. La posición es aquí la propiedad que se mide, no un atajo para
    /// encontrar una fila: por eso la última fila se marca en la página en vez de localizarse por orden.
    /// </summary>
    [Fact]
    public async Task Con_el_listado_desplazandose_por_su_cuenta_el_menu_de_la_ultima_fila_se_abre_entero()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirDocumentosAsync(contexto, 1100, "/documentos");
        // Se filtra por la entidad de una fila cualquiera para quedarse con pocos documentos y sin paginador.
        var entidad = await page.EvaluateAsync<string>("() => document.querySelector('table.tabla-datos tbody .nombre-fila-entidad').textContent.trim()");
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos?q={Uri.EscapeDataString(entidad)}");
        await Expect(page.Locator("table.tabla-datos tbody tr .menu-acciones-disparador")).Not.ToHaveCountAsync(0);
        await page.EvaluateAsync("() => document.querySelector('table.tabla-datos tbody tr:last-child').setAttribute('data-ultima-fila', '')");

        await Ayudas.AbrirMenuAccionesAsync(page.Locator("tr[data-ultima-fila] .menu-acciones-disparador"));
        var medicion = await page.EvaluateAsync<JsonElement>(MedirMenuAbierto);
        var vertical = medicion.GetProperty("vertical").GetInt32();
        var sobresale = medicion.GetProperty("sobresale").GetDouble();
        var donde = $"[1100 px, {medicion.GetProperty("filas").GetInt32()} fila(s), panel de {medicion.GetProperty("altoPanel").GetInt32()} px de alto]";
        salida.WriteLine($"MEDIDA {donde} desplazamiento vertical propio: {vertical} px; el panel sobresale {sobresale:0.#} px del envoltorio");

        // Si a este ancho el listado no se desplazara por su cuenta, el caso no estaría midiendo lo que dice.
        Assert.True(medicion.GetProperty("desplazaEnHorizontal").GetBoolean(), $"{donde} el listado no es un contenedor de desplazamiento a este ancho.");
        Assert.True(medicion.GetProperty("opciones").GetInt32() > 0, $"{donde} el menú no tiene opciones.");
        Assert.True(vertical <= 0, $"{donde} el menú abierto mete {vertical} px de desplazamiento vertical dentro del listado.");
        Assert.True(sobresale <= 0.5, $"{donde} el panel sobresale {sobresale:0.#} px por debajo del envoltorio del listado.");
        var tapadas = medicion.GetProperty("tapadas").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.True(tapadas.Count == 0, $"{donde} opciones que no se pueden pulsar: {string.Join(", ", tapadas)}");
    }

    private async Task<IPage> AbrirDocumentosAsync(IBrowserContext contexto, int anchoVentana, string ruta)
    {
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(anchoVentana, 800);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}{ruta}");
        await Expect(page.Locator("table.tabla-datos tbody tr .menu-acciones-disparador")).Not.ToHaveCountAsync(0);
        return page;
    }
}
