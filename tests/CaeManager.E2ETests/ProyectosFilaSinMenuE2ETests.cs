using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Patrón de listados sin menú «⋯» (decisión 2026-10-08), en Proyectos: un clic en la fila abre la
/// vista rápida, el recuento de técnicos enseña su lista y cada técnico abre el panel en la pestaña
/// «Técnicos», el icono 360 lleva a la página completa y la tecla «e» edita la fila enfocada. Lo
/// que bUnit no ve y aquí sí: que el clic de un control de dentro de la fila (el recuento, el
/// icono 360) no llega a la fila en un navegador real.
/// </summary>
[Collection("AppCollection")]
public class ProyectosFilaSinMenuE2ETests(WebAppFixture fixture)
{
    private static readonly Regex PaginaProyecto = new(@"/proyectos/[0-9a-f-]{36}$");

    private static ILocator Filas(IPage page) => page.Locator("table.tabla-proyectos tbody tr");

    private static ILocator FilaDe(IPage page, string nombreProyecto) => Filas(page).Filter(new LocatorFilterOptions
    { Has = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = nombreProyecto, Exact = true }) });

    /// <summary>
    /// Abre Proyectos y elige el primer Cliente empresarial del catálogo sembrado que tenga algún
    /// proyecto con técnicos activos; devuelve el nombre de uno de esos proyectos.
    /// </summary>
    private async Task<(IPage Page, string NombreProyecto)> AbrirProyectosConTecnicosAsync(IBrowserContext contexto)
    {
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 800);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/proyectos");

        var selector = page.GetByRole(AriaRole.Button,
            new PageGetByRoleOptions { NameRegex = new Regex(@"^Cliente(?:$|:)") });
        var menu = await AbrirMenuClienteAsync(selector, page);
        var opciones = (await menu.GetByRole(AriaRole.Menuitemradio).AllTextContentsAsync())
            .Select(o => o.Trim()).Where(o => o.Length > 0 && o != "Todos").ToArray();
        await page.Keyboard.PressAsync("Escape");
        await Expect(menu).ToBeHiddenAsync();

        var conTecnicos = Filas(page).Filter(new LocatorFilterOptions { Has = page.Locator(".ventana-contexto-disparador") });
        foreach (var nombre in opciones)
        {
            menu = await AbrirMenuClienteAsync(selector, page);
            var opcion = menu.GetByRole(AriaRole.Menuitemradio, new LocatorGetByRoleOptions { Name = nombre, Exact = true });
            await opcion.FocusAsync();
            await opcion.PressAsync("Space");
            await Expect(menu).ToBeHiddenAsync();
            await Expect(selector).ToHaveTextAsync("Cliente: " + nombre);
            // La lista de ese Cliente empresarial ya está pintada (tabla o estado vacío) antes de leerla.
            await Expect(page.Locator("table.tabla-proyectos, .estado-vacio")).ToBeVisibleAsync();

            var nombres = (await conTecnicos.Locator("button.nombre-proyecto").AllTextContentsAsync())
                .Select(n => n.Trim()).Where(n => n.Length > 0).ToArray();
            if (nombres.Length > 0)
                return (page, nombres[0]);
        }

        Assert.Fail("Control positivo: ningún Cliente sembrado tiene un proyecto con técnicos activos.");
        return default;
    }

    private static async Task<ILocator> AbrirMenuClienteAsync(ILocator selector, IPage page)
    {
        await Expect(selector).ToBeVisibleAsync();
        await selector.FocusAsync();
        await selector.PressAsync("Space");
        var panelId = await selector.GetAttributeAsync("aria-controls");
        Assert.False(string.IsNullOrWhiteSpace(panelId), "Control positivo: selector con panel identificado.");
        var menu = page.Locator("#" + panelId);
        await Expect(menu).ToBeVisibleAsync();
        return menu;
    }

    private static Task<int> PulsacionesDelNombreAsync(IPage page) =>
        page.EvaluateAsync<int>("() => window.__pulsacionesDelNombre");

    [Fact]
    public async Task El_clic_en_la_fila_abre_la_vista_rapida_un_tecnico_abre_su_pestana_y_el_icono_360_navega()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var (page, nombre) = await AbrirProyectosConTecnicosAsync(contexto);
        var fila = FilaDe(page, nombre);
        var panel = page.Locator("aside.panel-proyecto");
        await Expect(fila).ToHaveCountAsync(1);

        await Expect(page.Locator("table.tabla-proyectos .menu-acciones-disparador")).ToHaveCountAsync(0);

        // El oyente «pulsarFila» de atajos-lista.js pulsa el botón del nombre dentro del mismo
        // clic: contar esas pulsaciones en el documento dice, sin esperas, si un clic llegó a la
        // fila. La cuenta vive en window y aguanta los repintados (aquí no hay navegación).
        await page.EvaluateAsync(@"() => {
            window.__pulsacionesDelNombre = 0;
            document.addEventListener('click', e => {
                if (e.target.closest?.('.nombre-abre-vista-rapida')) window.__pulsacionesDelNombre++;
            }, true);
        }");

        // Un punto de la fila que no es ningún control: la celda de fechas.
        await fila.Locator("td.celda-fecha-proyecto").ClickAsync();
        Assert.Equal(1, await PulsacionesDelNombreAsync(page));
        await Expect(panel.Locator(".nombre-cabecera-panel-proyecto")).ToHaveTextAsync(nombre);
        await Expect(panel.Locator(".rejilla-info-proyecto")).ToBeVisibleAsync();

        // El icono 360 de la cabecera del panel es un enlace a la página completa.
        await Expect(panel.Locator(".cabecera-panel-proyecto a.boton-360-pagina")).ToHaveAttributeAsync("href", PaginaProyecto);
        await panel.Locator("button.cerrar-panel-proyecto").ClickAsync();
        await Expect(panel).ToHaveCountAsync(0);

        // El recuento de técnicos es un control de dentro: enseña su lista y NO abre la vista rápida.
        var ventana = fila.Locator(".celda-tecnicos-proyecto .ventana-contexto");
        await ventana.Locator(".ventana-contexto-disparador").ClickAsync();
        var lista = ventana.GetByRole(AriaRole.Group, new LocatorGetByRoleOptions { Name = "Técnicos activos", Exact = true });
        await Expect(lista).ToBeVisibleAsync();
        await Expect(panel).ToHaveCountAsync(0);

        // El título y el pie de la ventana no son botones, pero quien pulsa ahí está usando la
        // ventana: tampoco abren la vista rápida.
        await lista.Locator(".ventana-contexto-titulo").ClickAsync();
        await lista.Locator(".ventana-contexto-pie").ClickAsync();
        Assert.Equal(1, await PulsacionesDelNombreAsync(page));
        await Expect(lista).ToBeVisibleAsync();
        await Expect(panel).ToHaveCountAsync(0);

        // Un técnico de la lista abre el panel del proyecto en la pestaña «Técnicos».
        var tecnicos = (await lista.Locator(".ventana-contexto-elemento-texto").AllTextContentsAsync())
            .Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();
        Assert.True(tecnicos.Length > 0, "Control positivo: la ventana de contexto nombra a los técnicos activos.");
        await lista.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = tecnicos[0] }).ClickAsync();
        await Expect(panel.Locator(".nombre-cabecera-panel-proyecto")).ToHaveTextAsync(nombre);
        await Expect(panel.Locator(".lista-tecnicos-proyecto")).ToContainTextAsync(tecnicos[0]);

        // El icono 360 de la fila lleva a la página, sin abrir la vista rápida por el camino. Con
        // el panel cerrado: abierto, a este ancho tapa el final de la fila, y su cabecera tiene
        // su propio icono 360.
        await panel.Locator("button.cerrar-panel-proyecto").ClickAsync();
        await Expect(panel).ToHaveCountAsync(0);
        await fila.Locator("a.boton-360-pagina").ClickAsync();
        await page.WaitForURLAsync(PaginaProyecto);
        await Expect(panel).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task La_tecla_e_abre_en_edicion_la_fila_enfocada()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var (page, _) = await AbrirProyectosConTecnicosAsync(contexto);
        var panel = page.Locator("aside.panel-proyecto");

        // El foco sale de la pastilla del selector para que el módulo de atajos de lista reciba las teclas.
        await page.Locator("h1").ClickAsync();
        await page.Keyboard.PressAsync("j");
        var enfocada = page.Locator("table.tabla-proyectos tbody tr.fila-enfocada");
        await Expect(enfocada).ToHaveCountAsync(1);
        var nombre = (await enfocada.Locator("button.nombre-proyecto").InnerTextAsync()).Trim();

        await page.Keyboard.PressAsync("e");

        await Expect(panel.Locator(".nombre-cabecera-panel-proyecto")).ToHaveTextAsync(nombre);
        await Expect(panel.GetByRole(AriaRole.Textbox, new LocatorGetByRoleOptions { Name = "Nombre", Exact = true }))
            .ToHaveValueAsync(nombre);
    }
}
