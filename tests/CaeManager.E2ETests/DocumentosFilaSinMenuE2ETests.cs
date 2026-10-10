using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Patrón de listados sin menú «⋯» (decisión 2026-10-08), en Documentos: la fila no lleva menú, el
/// nombre abre la vista rápida y el icono 360 lleva a la página del TIPO de documento. Y lo que solo
/// se ve en un navegador: Alt + clic sobre el vencimiento copia la fecha de emisión sin encender las
/// letras de KeyTips, que se encienden con Alt pulsada y soltada sola.
/// <para>
/// El clic en cualquier punto de la fila lo atiende el oyente delegado de <c>atajos-lista.js</c>
/// (la tabla es un QuickGrid): se prueba aquí junto con su excepción, la ventana de contexto de
/// «Plataformas».
/// </para>
/// </summary>
[Collection("AppCollectionListados")]
public class DocumentosFilaSinMenuE2ETests(WebAppFixtureListados fixture)
{
    private async Task<IPage> AbrirDocumentosAsync(IBrowserContext contexto)
    {
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(1440, 900);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos");
        await Expect(page.Locator("table tbody tr.fila-pulsable")).Not.ToHaveCountAsync(0);
        return page;
    }

    [Fact]
    public async Task Alt_clic_en_el_vencimiento_copia_la_emision_y_no_enciende_las_letras()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        await contexto.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        var page = await AbrirDocumentosAsync(contexto);
        var html = page.Locator("html");
        var encendido = new Regex(".+");

        // Un documento con vencimiento: su botón copiable lleva las dos fechas.
        var fecha = page.Locator("table tbody button.texto-fecha-copiable[data-copiar-alt]").First;
        var vencimiento = (await fecha.InnerTextAsync()).Trim();
        var emision = await fecha.GetAttributeAsync("data-copiar-alt");
        Assert.Matches(@"^\d{2}/\d{2}/\d{4}$", emision);
        Assert.NotEqual(vencimiento, emision);

        // Control positivo: en esta pantalla, Alt sola SÍ enciende las letras.
        await page.Keyboard.PressAsync("Alt");
        await Expect(html).ToHaveAttributeAsync("data-keytips", "raiz");
        await page.Keyboard.PressAsync("Escape");
        await Expect(html).Not.ToHaveAttributeAsync("data-keytips", encendido);

        // Alt + clic: Playwright baja Alt, pulsa y la suelta. El aviso llega tras el viaje al
        // servidor, cuando la tecla ya se soltó: es la barrera de «las letras no se encendieron».
        await fecha.ClickAsync(new() { Modifiers = [KeyboardModifier.Alt] });
        await Expect(page.Locator(".toast").Filter(new() { HasText = "Fecha copiada" })).Not.ToHaveCountAsync(0);
        Assert.Equal(emision, await page.EvaluateAsync<string>("navigator.clipboard.readText()"));
        await Expect(html).Not.ToHaveAttributeAsync("data-keytips", encendido);
        // Ni abre la vista rápida. La protegen dos defensas del oyente de la fila a la vez —la fecha
        // es un control y el clic lleva modificador—: este aserto solo cae si faltan las dos.
        // El aviso de arriba es la barrera.
        await Expect(page.Locator(".workspace-panel")).ToHaveCountAsync(0);

        // Y un clic sin Alt sigue copiando el vencimiento.
        await fecha.ClickAsync();
        await page.WaitForFunctionAsync("async esperado => (await navigator.clipboard.readText()) === esperado", vencimiento);
    }

    [Fact]
    public async Task La_fila_no_tiene_menu_el_nombre_abre_la_vista_rapida_y_el_icono_360_lleva_al_tipo()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirDocumentosAsync(contexto);
        var fila = page.Locator("table tbody tr.fila-pulsable").First;
        var panel = page.Locator(".workspace-panel");

        await Expect(page.Locator("table .menu-acciones-disparador")).ToHaveCountAsync(0);

        // El icono 360 es un enlace real a la página del tipo de documento.
        var icono = fila.Locator("a.boton-360-pagina");
        await Expect(icono).ToHaveAttributeAsync("href", new Regex(@"^/documentos/tipos/[0-9a-f-]{36}$"));

        await fila.Locator("button.nombre-abre-vista-rapida").ClickAsync();
        await Expect(panel.Locator(".workspace-titulo-entidad")).ToBeVisibleAsync();

        // Con el panel cerrado: abierto, a este ancho puede tapar el final de la fila.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos");
        await Expect(panel).ToHaveCountAsync(0);
        await page.Locator("table tbody tr.fila-pulsable").First.Locator("a.boton-360-pagina").ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/documentos/tipos/[0-9a-f-]{36}$"));
        await Expect(page.Locator("h1")).Not.ToHaveCountAsync(0);
    }

    /// <summary>
    /// El clic de fila en sí, y su excepción. Depende del oyente delegado <c>pulsarFila</c> de
    /// <c>atajos-lista.js</c>: un clic en un punto sin controles abre la vista rápida; uno dentro
    /// del panel de la ventana de contexto de «Plataformas» (su título no es un botón), no.
    /// </summary>
    [Fact]
    public async Task El_clic_en_un_punto_sin_controles_abre_la_vista_rapida_y_el_de_dentro_de_la_ventana_de_plataformas_no()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(1440, 900);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        var panel = page.Locator(".workspace-panel");

        // La ventana de contexto solo existe en los documentos con acreditaciones en plataformas,
        // que la siembra pone en documentos de Empresa: se acota a ese ámbito y, si aun así hay
        // más de una página, se pide el tamaño de página mayor para no depender del orden.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos?Ambito=Empresa");
        await Expect(page.Locator("table tbody tr.fila-pulsable")).Not.ToHaveCountAsync(0);
        var tamano = page.Locator("select.paginador-tamano-select");
        if (await tamano.CountAsync() == 1)
        {
            var mayor = await tamano.EvaluateAsync<string>("s => String(Math.max(...[...s.options].map(o => Number(o.value))))");
            if (await tamano.InputValueAsync() != mayor)
            {
                // Barrera de la recarga: con más filas por página, el «Página 1 de N» cambia.
                var textoPaginador = page.Locator(".paginador-texto");
                var antes = await textoPaginador.InnerTextAsync();
                await tamano.SelectOptionAsync(mayor);
                await Expect(textoPaginador).Not.ToHaveTextAsync(antes);
            }
        }

        var filas = page.Locator("table tbody tr.fila-pulsable")
            .Filter(new LocatorFilterOptions { Has = page.Locator(".ventana-contexto.documentos-plataformas") });
        await Expect(filas).Not.ToHaveCountAsync(0);
        var fila = filas.First;
        var titulo = fila.Locator(".ventana-contexto.documentos-plataformas .ventana-contexto-panel .ventana-contexto-titulo");

        // Cuenta, en el propio navegador y sin viaje al servidor, las pulsaciones que recibe el
        // nombre de una fila: es lo que hace el oyente cuando decide que el clic era de la fila.
        await page.EvaluateAsync(@"() => {
            window.__pulsacionesDelNombre = 0;
            document.addEventListener('click', e => {
                if (e.target.closest?.('.nombre-abre-vista-rapida')) window.__pulsacionesDelNombre++;
            }, true);
        }");

        // El clic se despacha: lo que se mide es que el oyente de la fila descarta lo que nace
        // dentro de la ventana, no que la ventana esté a la vista (se sostiene por :hover y
        // :focus-within). El oyente es síncrono: al volver del despacho, o pulsó el nombre o no.
        await Expect(titulo).ToHaveCountAsync(1);
        await titulo.DispatchEventAsync("click");
        Assert.Equal(0, await page.EvaluateAsync<int>("() => window.__pulsacionesDelNombre"));
        await Expect(panel).ToHaveCountAsync(0);

        // Y en esa misma fila, con el ratón de verdad, un punto sin controles —la celda del
        // estado— sí pulsa el nombre y abre la vista rápida.
        await fila.Locator("td.col-estado").ClickAsync();
        Assert.Equal(1, await page.EvaluateAsync<int>("() => window.__pulsacionesDelNombre"));
        await Expect(panel.Locator(".workspace-titulo-entidad")).ToBeVisibleAsync();
    }
}
