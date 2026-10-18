using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Patrón de listados sin menú «⋯» (decisión 2026-10-08), en Clientes empresariales. La lista son filas en
/// acordeón (sin QuickGrid, 1:1 con «Lista Clientes TALVEG.dc.html»): la cabecera de cada fila lleva
/// «fila-pulsable» y un clic en cualquier punto que no sea un control abre la vista rápida; los controles de
/// dentro (CIF copiable, recuento de Centros, icono 360) no la abren. Solo se prueba aquí, en un navegador
/// real, porque la propagación del clic no existe para bUnit. La lista ya no tiene celda «Gestor CAE» (el
/// Gestor CAE de referencia se gestiona en Cliente 360) y «Editar» vive en el lápiz del panel. El reparto por
/// rol lo prueba <c>ClientesListaGen2Tests</c> (bUnit) y el lápiz, <c>ClienteWorkspacePanelLapizTests</c>.
/// </summary>
[Collection("AppCollectionListados")]
public class ClientesFilaSinMenuE2ETests(WebAppFixtureListados fixture)
{
    private const string Lapiz = "button[aria-label='Editar la identidad del Cliente']";

    /// <summary>
    /// Crea un Cliente empresarial propio del test y deja la lista acotada a él: el Tenant del
    /// Administrador arranca sin datos, y con una sola fila «la primera» no depende del orden.
    /// </summary>
    private async Task<(IPage Page, string RazonSocial)> AbrirClientesConUnClienteAsync(IBrowserContext contexto, int semillaCif)
    {
        var razonSocial = $"FilaSinMenu {Guid.NewGuid().ToString("N")[..8]}";
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 800);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");

        // Con la lista vacía el botón de alta se pinta dos veces (cabecera y estado vacío): vale cualquiera.
        var drawer = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Nuevo Cliente", Exact = true });
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "+ Nuevo Cliente", Exact = true }).First.ClickAsync();
        await drawer.GetByLabel("Razón social").FillAsync(razonSocial);
        await drawer.GetByLabel("Identificación fiscal", new LocatorGetByLabelOptions { Exact = true })
            .FillAsync(Ayudas.GenerarCifValido(semillaCif));
        await drawer.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Guardar", Exact = true }).ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        return (page, razonSocial);
    }

    /// <summary>Acota la lista al Cliente empresarial del test y devuelve su fila, ya única.</summary>
    private static async Task<ILocator> FiltrarPorAsync(IPage page, string razonSocial)
    {
        await page.GetByPlaceholder("Filtrar esta pantalla: nombre").FillAsync(razonSocial);
        var filas = page.Locator(".tarjeta-fila-acordeon-cabecera.fila-pulsable");
        await Expect(filas).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 15_000 });
        await Expect(filas.Locator(".nombre-abre-vista-rapida")).ToHaveTextAsync(razonSocial);
        return filas;
    }

    /// <summary>
    /// «j» sin fila enfocada enfoca la primera. Antes hay que sacar el foco del buscador (una «j»
    /// ahí se escribe en el campo), y salir de él relanza la carga, que limpia la fila enfocada;
    /// además la lista vuelve a pedir la página cuando el total cambia. Una «j» que
    /// llegue antes de esas cargas pierde el foco sin que nada en el DOM lo anuncie: se reintenta
    /// hasta que la fila enfocada aguante. Mientras no haya fila enfocada, repetir «j» vuelve a la
    /// primera, así que el reintento no cambia qué fila se prueba.
    /// </summary>
    private static async Task EnfocarLaFilaConJAsync(IPage page)
    {
        await page.Keyboard.PressAsync("Tab");
        await page.WaitForTimeoutAsync(400);

        var enfocada = page.Locator(".lista-filas-acordeon .tarjeta-fila-acordeon.fila-enfocada");
        for (var intento = 0; intento < 5; intento++)
        {
            if (await enfocada.CountAsync() == 0)
                await page.Keyboard.PressAsync("j");
            await page.WaitForTimeoutAsync(400);
            if (await enfocada.CountAsync() == 1)
            {
                await page.WaitForTimeoutAsync(600);
                if (await enfocada.CountAsync() == 1) return;
            }
        }

        await Ayudas.EsperarFocoRealEnFilaAsync(enfocada);
    }

    [Fact]
    public async Task Los_controles_de_la_fila_no_abren_la_vista_rapida_y_el_lapiz_edita()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        await contexto.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        var (page, razonSocial) = await AbrirClientesConUnClienteAsync(contexto, 9_993_101);
        var fila = await FiltrarPorAsync(page, razonSocial);
        var panel = page.Locator(".workspace-panel");

        await Expect(page.Locator(".lista-filas-acordeon .menu-acciones-disparador")).ToHaveCountAsync(0);

        // El CIF se copia y NO abre el panel: es un control de dentro de la fila.
        var cif = fila.Locator(".boton-copiar-en-linea");
        var valorCif = (await cif.InnerTextAsync()).Trim();
        await cif.ClickAsync();
        await Expect(page.Locator(".toast", new PageLocatorOptions { HasText = "Se copió el CIF" })).ToHaveCountAsync(1);
        Assert.Equal(valorCif, await page.EvaluateAsync<string>("navigator.clipboard.readText()"));
        await Expect(panel).ToHaveCountAsync(0);

        // El icono 360 de la fila lleva a la página, sin abrir la vista rápida por el camino
        // (si el clic llegara a la fila, la URL de destino llevaría el «ctx» del panel). Con el
        // panel cerrado: abierto, a este ancho tapa el final de la fila.
        await fila.Locator("a.boton-360-pagina").ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/clientes/[0-9a-f-]{36}$"));
        await Expect(panel).ToHaveCountAsync(0);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");
        fila = await FiltrarPorAsync(page, razonSocial);

        // El nombre abre la vista rápida, cuya cabecera lleva su propio icono 360 a la página
        // completa y el lápiz, que entra en edición sin salir de la lista.
        await fila.Locator(".nombre-abre-vista-rapida").ClickAsync();
        await Expect(panel.Locator(".workspace-titulo-entidad")).ToHaveTextAsync(razonSocial);
        await Expect(panel.Locator("a.boton-360-pagina")).ToHaveAttributeAsync("href", new Regex(@"^/clientes/[0-9a-f-]{36}$"));
        await panel.Locator(Lapiz).ClickAsync();
        await Expect(panel.Locator(".workspace-acciones-edicion")).ToBeVisibleAsync();
        await Expect(panel.Locator(Lapiz)).ToHaveCountAsync(0);
    }

    /// <summary>
    /// El clic de fila en sí: el manejador de clic de la cabecera de la fila abre la vista rápida desde cualquier
    /// punto que no sea un control.
    /// </summary>
    [Fact]
    public async Task El_clic_en_un_punto_sin_controles_de_la_fila_abre_la_vista_rapida()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var (page, razonSocial) = await AbrirClientesConUnClienteAsync(contexto, 9_993_102);
        var fila = await FiltrarPorAsync(page, razonSocial);
        var panel = page.Locator(".workspace-panel");
        await Expect(panel).ToHaveCountAsync(0);

        // La celda «Estado documental» de un Cliente empresarial sin alertas (el recién creado) no tiene
        // ningún control: solo el estado. Con alertas, el motivo es el disparador de su ventana de incidencias.
        await fila.Locator(".col-estado").ClickAsync();

        await Expect(panel.Locator(".workspace-titulo-entidad")).ToHaveTextAsync(razonSocial);
        await Expect(panel.Locator(".workspace-acciones-edicion")).ToHaveCountAsync(0);
    }

    /// <summary>
    /// El recuento de Centros abre una ventana de contexto; su título no es un botón, pero quien
    /// pulsa ahí está usando la ventana, no la fila: la ventana interactiva no deja subir el clic. Entra como Gestor CAE porque su cartera sembrada trae Clientes empresariales con
    /// Centros (el Tenant propietario del Administrador de pruebas arranca vacío).
    /// </summary>
    [Fact]
    public async Task El_clic_dentro_de_la_ventana_de_Centros_no_abre_la_vista_rapida()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 800);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");
        var panel = page.Locator(".workspace-panel");

        var filas = page.Locator(".tarjeta-fila-acordeon-cabecera.fila-pulsable")
            .Filter(new LocatorFilterOptions { Has = page.Locator(".ranura-centros-cliente .ventana-contexto") });
        await Expect(filas).Not.ToHaveCountAsync(0);
        var fila = filas.First;
        var razonSocial = (await fila.Locator(".nombre-abre-vista-rapida").InnerTextAsync()).Trim();
        var titulo = fila.Locator(".ranura-centros-cliente .ventana-contexto-panel .ventana-contexto-titulo");

        // El clic se despacha: se mide que la ventana no abre la fila, no que esté a la vista (se sostiene por
        // :hover y :focus-within). La ventana interactiva corta la propagación hacia el clic de la fila.
        await Expect(titulo).ToHaveCountAsync(1);
        await titulo.DispatchEventAsync("click");
        await Expect(panel).ToHaveCountAsync(0);

        // Control positivo, por el mismo camino: el mismo clic despachado sobre la celda que contiene la ventana
        // —un punto de la fila sin control— sí abre la vista rápida.
        await fila.Locator(".celda-centros-cliente").DispatchEventAsync("click");
        await Expect(panel.Locator(".workspace-titulo-entidad")).ToHaveTextAsync(razonSocial);
    }
    [Fact]
    public async Task Enter_abre_la_vista_rapida_de_la_fila_enfocada_y_la_tecla_e_la_abre_en_edicion()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var (page, razonSocial) = await AbrirClientesConUnClienteAsync(contexto, 9_993_103);
        var panel = page.Locator(".workspace-panel");

        await FiltrarPorAsync(page, razonSocial);
        await EnfocarLaFilaConJAsync(page);
        await page.Keyboard.PressAsync("Enter");
        await Expect(panel.Locator(".workspace-titulo-entidad")).ToHaveTextAsync(razonSocial);
        await Expect(panel.Locator(".workspace-acciones-edicion")).ToHaveCountAsync(0);

        // De vuelta en la lista sin panel, «e» sobre la fila enfocada abre la ficha ya en edición.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");
        await Expect(panel).ToHaveCountAsync(0);
        await FiltrarPorAsync(page, razonSocial);
        await EnfocarLaFilaConJAsync(page);
        await page.Keyboard.PressAsync("e");

        await Expect(panel.Locator(".workspace-titulo-entidad")).ToHaveTextAsync(razonSocial);
        await Expect(panel.Locator(".workspace-acciones-edicion")).ToBeVisibleAsync();
    }
}
