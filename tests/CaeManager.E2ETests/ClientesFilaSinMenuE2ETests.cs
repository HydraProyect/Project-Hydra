using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Patrón de listados sin menú «⋯» (decisión 2026-10-08), en Clientes empresariales. La lista es
/// un QuickGrid, que no ofrece clic de fila: la fila lleva «fila-pulsable» y es
/// <c>atajos-lista.js</c> quien, ante un clic en cualquier punto que no sea un control, pulsa el
/// nombre de la fila. Esa mitad no existe para bUnit: solo se prueba aquí, en un navegador real,
/// junto con que los controles de dentro (CIF copiable, celda «Gestor CAE», icono 360) no abren
/// la vista rápida. La celda «Gestor CAE» es la única entrada al formulario «Editar» de la lista
/// y solo se ofrece a quien puede reasignar: por eso el recorrido entra como Administrador. El
/// reparto por rol lo prueba <c>ClientesListaGen2Tests</c> (bUnit) y el lápiz del panel,
/// <c>ClienteWorkspacePanelLapizTests</c>.
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
        var filas = page.Locator("tbody tr.fila-pulsable");
        await Expect(filas).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 15_000 });
        await Expect(filas.Locator(".nombre-abre-vista-rapida")).ToHaveTextAsync(razonSocial);
        return filas;
    }

    /// <summary>
    /// «j» sin fila enfocada enfoca la primera. Antes hay que sacar el foco del buscador (una «j»
    /// ahí se escribe en el campo), y salir de él relanza la carga, que limpia la fila enfocada;
    /// además QuickGrid vuelve a pedir la página por su cuenta cuando el total cambia. Una «j» que
    /// llegue antes de esas cargas pierde el foco sin que nada en el DOM lo anuncie: se reintenta
    /// hasta que la fila enfocada aguante. Mientras no haya fila enfocada, repetir «j» vuelve a la
    /// primera, así que el reintento no cambia qué fila se prueba.
    /// </summary>
    private static async Task EnfocarLaFilaConJAsync(IPage page)
    {
        await page.Keyboard.PressAsync("Tab");
        await page.WaitForTimeoutAsync(400);

        var enfocada = page.Locator("tbody tr.fila-enfocada");
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

        await Expect(enfocada).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task Los_controles_de_la_fila_no_abren_la_vista_rapida_la_celda_Gestor_CAE_abre_Editar_y_el_lapiz_edita()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        await contexto.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        var (page, razonSocial) = await AbrirClientesConUnClienteAsync(contexto, 9_993_101);
        var fila = await FiltrarPorAsync(page, razonSocial);
        var panel = page.Locator(".workspace-panel");
        var editar = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Editar Cliente", Exact = true });

        await Expect(page.Locator("tbody .menu-acciones-disparador")).ToHaveCountAsync(0);

        // El CIF se copia y NO abre el panel: es un control de dentro de la fila.
        var cif = fila.Locator(".boton-copiar-en-linea");
        var valorCif = (await cif.InnerTextAsync()).Trim();
        await cif.ClickAsync();
        await Expect(page.Locator(".toast", new PageLocatorOptions { HasText = "Se copió el CIF" })).ToHaveCountAsync(1);
        Assert.Equal(valorCif, await page.EvaluateAsync<string>("navigator.clipboard.readText()"));
        await Expect(panel).ToHaveCountAsync(0);

        // La celda «Gestor CAE» abre el formulario «Editar» de ESTE Cliente empresarial (el que
        // lleva el selector de Gestor CAE de referencia), y no la vista rápida.
        await fila.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Cambiar el Gestor CAE de referencia de {razonSocial}" }).ClickAsync();
        await Expect(editar).ToBeVisibleAsync();
        await Expect(editar.GetByLabel("Razón social")).ToHaveValueAsync(razonSocial);
        await Expect(panel).ToHaveCountAsync(0);
        await editar.Locator(".drawer-cerrar").ClickAsync();
        await editar.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

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
    /// El clic de fila en sí. Depende del oyente delegado <c>pulsarFila</c> de
    /// <c>atajos-lista.js</c>: sin él, la fila de un QuickGrid no reacciona y este test es rojo.
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
        await fila.Locator("td.col-estado").ClickAsync();

        await Expect(panel.Locator(".workspace-titulo-entidad")).ToHaveTextAsync(razonSocial);
        await Expect(panel.Locator(".workspace-acciones-edicion")).ToHaveCountAsync(0);
    }

    /// <summary>
    /// El recuento de Centros abre una ventana de contexto; su título no es un botón, pero quien
    /// pulsa ahí está usando la ventana, no la fila: el oyente <c>pulsarFila</c> excluye el panel
    /// entero. Entra como Gestor CAE porque su cartera sembrada trae Clientes empresariales con
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

        var filas = page.Locator("tbody tr.fila-pulsable")
            .Filter(new LocatorFilterOptions { Has = page.Locator(".ranura-centros-cliente .ventana-contexto") });
        await Expect(filas).Not.ToHaveCountAsync(0);
        var fila = filas.First;
        var razonSocial = (await fila.Locator(".nombre-abre-vista-rapida").InnerTextAsync()).Trim();
        var titulo = fila.Locator(".ranura-centros-cliente .ventana-contexto-panel .ventana-contexto-titulo");

        // Cuenta, en el propio navegador y sin viaje al servidor, las pulsaciones que recibe el
        // nombre de una fila: es lo que hace el oyente cuando decide que el clic era de la fila.
        await page.EvaluateAsync(@"() => {
            window.__pulsacionesDelNombre = 0;
            document.addEventListener('click', e => {
                if (e.target.closest?.('.nombre-abre-vista-rapida')) window.__pulsacionesDelNombre++;
            }, true);
        }");

        // El clic se despacha: se mide el oyente de la fila, no que la ventana esté a la vista (se
        // sostiene por :hover y :focus-within). El oyente es síncrono: al volver del despacho, o
        // pulsó el nombre o no lo pulsó.
        await Expect(titulo).ToHaveCountAsync(1);
        await titulo.DispatchEventAsync("click");
        Assert.Equal(0, await page.EvaluateAsync<int>("() => window.__pulsacionesDelNombre"));
        await Expect(panel).ToHaveCountAsync(0);

        // Control positivo, por el mismo camino: el mismo clic despachado sobre la celda que
        // contiene la ventana —un punto de la fila sin control— sí pulsa el nombre y abre la vista rápida.
        await fila.Locator("td").Filter(new LocatorFilterOptions { Has = page.Locator(".ranura-centros-cliente") })
            .DispatchEventAsync("click");
        Assert.Equal(1, await page.EvaluateAsync<int>("() => window.__pulsacionesDelNombre"));
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
