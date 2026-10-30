using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Patrón de listados sin menú «⋯» (decisión 2026-10-08), en Trabajadores. La lista tiene filas de detalle independientes.
/// La fila del Trabajador lleva «fila-pulsable» y es
/// <c>atajos-lista.js</c> quien, ante un clic en cualquier punto que no sea un control, pulsa
/// el nombre de la fila. Esa mitad no existe para bUnit: solo se prueba aquí, en un navegador
/// real, junto con que los controles de dentro (DNI copiable, icono 360) no abren la vista
/// rápida. El formulario de edición lo prueba <c>TrabajadorWorkspacePanelLapizTests</c> (bUnit).
/// </summary>
[Collection("AppCollectionListados")]
public class TrabajadoresFilaSinMenuE2ETests(WebAppFixtureListados fixture)
{
    private const string Lapiz = "button[aria-label='Editar información del trabajador']";

    // La lista sembrada no tiene un trabajador de nombre estable que sirva de ancla: el
    // recorrido vale para cualquier fila, así que se toma la primera, y su celda «Empresa» (la
    // tercera) como punto de la fila que no es ningún control.
    private static ILocator PrimeraFila(IPage page) => page.Locator(".marco-lista-trabajadores .fila-pulsable").First;
    private static ILocator CeldaSinControles(ILocator fila) => fila.Locator(":scope > :nth-child(3)");

    private async Task<IPage> AbrirTrabajadoresAsync(IBrowserContext contexto)
    {
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 800);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
        await Expect(page.Locator(".marco-lista-trabajadores .fila-pulsable")).Not.ToHaveCountAsync(0);
        return page;
    }

    /// <summary>La carga observada termina antes de enfocar la cabecera y recorrer los propietarios.</summary>
    private static async Task EnfocarPrimeraFilaConJAsync(IPage page)
    {
        await Ayudas.EsperarListaListaAsync(page.Locator(".marco-lista-trabajadores .lista-filas-acordeon"));
        await Ayudas.EnfocarFilaAsync(page, page.Locator(".cabecera-columnas-trabajadores .col-estado button.col-title"), page.Locator(".marco-lista-trabajadores .fila-enfocada"));
    }

    [Fact]
    public async Task La_incidencia_desplegada_abre_el_formulario_sin_abrir_la_vista_rapida()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirTrabajadoresAsync(contexto);
        await Ayudas.EsperarListaListaAsync(page.Locator(".marco-lista-trabajadores .lista-filas-acordeon"));
        var candidatos = await page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Desplegar incidencias de ") }).AllAsync();
        Assert.NotEmpty(candidatos); // La fixture debe ofrecer incidencias; no se convierte su ausencia en éxito.
        foreach (var candidato in candidatos)
        {
            var etiqueta = await candidato.GetAttributeAsync("aria-label");
            Assert.NotNull(etiqueta);
            var chevron = page.GetByRole(AriaRole.Button, new() { Name = etiqueta, Exact = true });
            if (await chevron.CountAsync() != 1) continue; // Puede haber dos Trabajadores con el mismo nombre.
            await chevron.ClickAsync();
            await Expect(chevron).ToHaveAttributeAsync("aria-expanded", "true");
            var detalle = page.Locator(".marco-lista-trabajadores .tarjeta-fila-acordeon-contenido");
            await Expect(detalle).ToHaveCountAsync(1);
            await Expect(detalle).Not.ToHaveClassAsync(new Regex(@"\bfila-pulsable\b"));
            var acciones = await detalle.GetByRole(AriaRole.Button).AllAsync();
            Assert.NotEmpty(acciones);
            foreach (var accion in acciones)
            {
                var nombre = await accion.GetAttributeAsync("aria-label");
                Assert.NotNull(nombre); // El botón se localiza por su propio nombre accesible («Gestionar {documento} de {trabajador}»), no por el texto «Gestionar».
                var corregir = detalle.GetByRole(AriaRole.Button, new() { Name = nombre, Exact = true });
                if (await corregir.CountAsync() != 1) continue;
                await corregir.ClickAsync();
                await Expect(page.GetByRole(AriaRole.Dialog, new() { NameRegex = new Regex("^(Renovar|Nuevo) documento$") })).ToBeVisibleAsync();
                await Expect(page.Locator(".workspace-panel")).ToHaveCountAsync(0);
                return;
            }
            await chevron.ClickAsync();
        }
        Assert.Fail("La fixture no ofrece una incidencia corregible identificable por nombre accesible único.");
    }

    [Fact]
    public async Task El_clic_en_la_fila_abre_la_vista_rapida_los_controles_no_y_el_lapiz_edita()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        await contexto.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        var page = await AbrirTrabajadoresAsync(contexto);
        var fila = PrimeraFila(page);
        var nombre = (await fila.Locator(".nombre-abre-vista-rapida").InnerTextAsync()).Trim();
        var panel = page.Locator(".workspace-panel");

        await Expect(page.Locator(".marco-lista-trabajadores .menu-acciones-disparador")).ToHaveCountAsync(0);

        // El DNI se copia y NO abre el panel: es un control de dentro de la fila.
        var dni = fila.Locator(".boton-copiar-en-linea");
        var valorDni = (await dni.InnerTextAsync()).Trim();
        await dni.ClickAsync();
        await Expect(page.Locator(".toast")).ToContainTextAsync("Se copió el DNI");
        Assert.Equal(valorDni, await page.EvaluateAsync<string>("navigator.clipboard.readText()"));
        await Expect(panel).ToHaveCountAsync(0);

        // El icono 360 de la fila lleva a la página, sin abrir la vista rápida por el camino
        // (si el clic llegara a la fila, la URL de destino llevaría el «ctx» del panel). Con el
        // panel cerrado: abierto, a este ancho tapa el final de la fila.
        await fila.Locator("a.boton-360-pagina").ClickAsync();
        // Aserción de URL, no WaitForURLAsync: la navegación mejorada de Blazor no es un documento nuevo y la espera de «load» caducaba a los 30 s (#1843).
        await Expect(page).ToHaveURLAsync(new Regex(@"/trabajadores/[0-9a-f-]{36}$"), new PageAssertionsToHaveURLOptions { Timeout = 30_000 });
        await Expect(panel).ToHaveCountAsync(0);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");

        // Un punto de la fila que no es ningún control: la celda «Empresa».
        await CeldaSinControles(fila).ClickAsync();
        await Expect(panel.Locator(".workspace-titulo-entidad")).ToHaveTextAsync(nombre);

        // La cabecera del panel lleva su propio icono 360 a la página completa y el lápiz.
        await Expect(panel.Locator("a.boton-360-pagina")).ToHaveAttributeAsync("href", new Regex(@"^/trabajadores/[0-9a-f-]{36}$"));
        await panel.Locator(Lapiz).ClickAsync();
        await Expect(panel.Locator(".workspace-acciones-edicion")).ToBeVisibleAsync();
        await Expect(panel.Locator(Lapiz)).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Enter_abre_la_vista_rapida_de_la_fila_enfocada_y_la_tecla_e_la_abre_en_edicion()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await AbrirTrabajadoresAsync(contexto);
        var panel = page.Locator(".workspace-panel");

        await EnfocarPrimeraFilaConJAsync(page);
        var nombre = (await page.Locator(".marco-lista-trabajadores .fila-enfocada .nombre-abre-vista-rapida").InnerTextAsync()).Trim();

        await page.Keyboard.PressAsync("Enter");
        await Expect(panel.Locator(".workspace-titulo-entidad")).ToHaveTextAsync(nombre);
        await Expect(panel.Locator(".workspace-acciones-edicion")).ToHaveCountAsync(0);

        // De vuelta en la lista sin panel, «e» sobre la fila enfocada abre la ficha ya en edición.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
        await Expect(panel).ToHaveCountAsync(0);
        await EnfocarPrimeraFilaConJAsync(page);
        await page.Keyboard.PressAsync("e");

        await Expect(panel.Locator(".workspace-acciones-edicion")).ToBeVisibleAsync();
    }
}
