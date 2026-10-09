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
/// El clic en cualquier punto de la fila no se prueba aquí: en una tabla QuickGrid lo atiende el
/// oyente delegado de <c>atajos-lista.js</c>, que llega con la adopción de Trabajadores.
/// </para>
/// </summary>
[Collection("AppCollection")]
public class DocumentosFilaSinMenuE2ETests(WebAppFixture fixture)
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
}
