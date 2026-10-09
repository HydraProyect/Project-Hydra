using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Patrón de listados sin menú «⋯» (decisión 2026-10-08), en Visitas. La lista es un QuickGrid,
/// que no ofrece clic de fila: la fila lleva «fila-pulsable» y es <c>atajos-lista.js</c> quien,
/// ante un clic en cualquier punto que no sea un control, pulsa el botón de la vista rápida de la
/// fila (sus fechas). Esa mitad no existe para bUnit: solo se prueba aquí, en un navegador real,
/// junto con que los controles de dentro (recuento de trabajadores con su ventana de contexto,
/// icono 360) no abren la vista rápida. El resto lo prueba <c>VisitasGen2Tests</c> (bUnit).
/// </summary>
[Collection("AppCollection")]
public class VisitasFilaSinMenuE2ETests(WebAppFixture fixture)
{
    private static readonly Regex PaginaVisita = new(@"/visitas/[0-9a-f-]{36}$");

    private static ILocator PanelVisita(IPage page) =>
        page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Detalle de la visita", Exact = true });

    private static ILocator FormularioEdicion(IPage page) =>
        page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Editar visita", Exact = true });

    /// <summary>
    /// Abre /visitas y da de alta una Visita propia del test, por la interfaz, en el primer Centro
    /// que ofrece el formulario y con el primer Trabajador de su lista. La fecha, lejana y al azar,
    /// es lo que distingue su fila: ningún localizador depende de la siembra ni de las Visitas que
    /// dejen otros tests en ese Centro.
    /// </summary>
    private async Task<(IPage Page, ILocator Fila)> AbrirConUnaVisitaAsync(IBrowserContext contexto)
    {
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 800);
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/visitas");

        var fecha = Ayudas.HoyDeNegocio().AddDays(400 + Random.Shared.Next(3000));
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "+ Nueva visita", Exact = true }).ClickAsync();
        var alta = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Nueva visita", Exact = true });
        await Expect(alta).ToBeVisibleAsync();
        await alta.GetByLabel("Centro", new LocatorGetByLabelOptions { Exact = true }).SelectOptionAsync(new SelectOptionValue { Index = 1 });
        await alta.GetByLabel("Fecha de inicio", new LocatorGetByLabelOptions { Exact = true }).FillAsync(fecha.ToString("yyyy-MM-dd"));
        await alta.GetByLabel("Fecha de fin", new LocatorGetByLabelOptions { Exact = true }).FillAsync(fecha.ToString("yyyy-MM-dd"));
        // CrearVisitaCommandValidator exige al menos un Trabajador que entre: vale cualquiera, el primero.
        await alta.Locator(".lista-seleccion-multiple input[type=checkbox]").First.CheckAsync();
        await alta.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Guardar", Exact = true }).ClickAsync();
        await Expect(alta).ToBeHiddenAsync();

        // La fila se localiza por el nombre accesible de su botón de vista rápida (que acaba en la
        // fecha), no por «fila-pulsable»: si la marca faltara, lo que debe fallar es el clic en la
        // fila, no la preparación.
        // Las barras de la fecha van como \x2F: Playwright serializa la expresión entre barras y una
        // barra literal le corta el selector (InvalidSelectorError).
        var fechaEnPatron = fecha.ToString("dd/MM/yyyy").Replace("/", @"\x2F");
        var fila = page.Locator("tbody tr").Filter(new LocatorFilterOptions
        {
            Has = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                NameRegex = new Regex("^Abrir la vista rápida de la visita a .*, " + fechaEnPatron + "$")
            })
        });
        await Expect(fila).ToHaveCountAsync(1);
        return (page, fila);
    }

    /// <summary>
    /// «j» hasta que la fila del test es la enfocada. Se insiste porque QuickGrid vuelve a pedir la
    /// página por su cuenta cuando el total cambia (aquí, tras el alta), y cada carga limpia la fila
    /// enfocada: una «j» que llegue antes de esa segunda carga pierde el foco sin que nada en el DOM
    /// lo anuncie, y la siguiente vuelve a empezar por la primera fila.
    /// </summary>
    private static async Task EnfocarLaFilaConJAsync(IPage page, ILocator fila)
    {
        const string EstaEnfocada = "f => f.classList.contains('fila-enfocada')";
        for (var intento = 0; intento < 80; intento++)
        {
            if (await fila.EvaluateAsync<bool>(EstaEnfocada))
            {
                await page.WaitForTimeoutAsync(600);
                if (await fila.EvaluateAsync<bool>(EstaEnfocada)) return;
                continue;
            }

            await page.Keyboard.PressAsync("j");
            await page.WaitForTimeoutAsync(250);
        }

        await Expect(fila).ToHaveClassAsync(new Regex("fila-enfocada"));
    }

    [Fact]
    public async Task El_clic_en_la_fila_abre_la_vista_rapida_los_controles_no_y_el_lapiz_edita()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var (page, fila) = await AbrirConUnaVisitaAsync(contexto);
        var panel = PanelVisita(page);

        await Expect(page.Locator("tbody .menu-acciones-disparador")).ToHaveCountAsync(0);

        // El recuento de trabajadores es un control de dentro: enseña quién entra y NO abre la vista
        // rápida. Tampoco el título ni el pie de su ventana, que no son botones: quien pulsa ahí está
        // usando la ventana, no la fila.
        // La fila del test es la última de la lista (su fecha es la más lejana): se centra antes de
        // abrir la ventana, que se sostiene por :hover y :focus-within. Si su pie quedara fuera de la
        // vista, el desplazamiento del propio clic la sacaría de debajo del puntero y se cerraría.
        await fila.EvaluateAsync("f => f.scrollIntoView({ block: 'center' })");
        var ventana = fila.Locator(".ventana-contexto.visitas-recuento-trabajadores");
        await ventana.Locator(".ventana-contexto-disparador").ClickAsync();
        var lista = ventana.GetByRole(AriaRole.Group, new LocatorGetByRoleOptions { Name = "Trabajadores asignados", Exact = true });
        await Expect(lista).ToBeVisibleAsync();
        // Los dos clics se despachan en vez de darse con el ratón: lo que aquí se mide es que el
        // oyente de la fila descarta lo que nace dentro de la ventana, no que la ventana siga a la
        // vista (se sostiene por :hover y :focus-within, y con el ratón real se cerraba entre un clic
        // y el siguiente una vez de cada dos, sin relación con el oyente).
        await lista.Locator(".ventana-contexto-titulo").DispatchEventAsync("click");
        await lista.Locator(".ventana-contexto-pie").DispatchEventAsync("click");
        // Barrera: si el oyente pulsara el botón de la fila, el panel llegaría tras la vuelta al
        // servidor; sin esta espera, la ausencia se cumpliría antes de que pudiera aparecer.
        await page.WaitForTimeoutAsync(1000);
        await Expect(panel).ToHaveCountAsync(0);

        // Un punto de la fila que no es ningún control: la celda de la documentación.
        await fila.Locator("td.col-estado").ClickAsync();
        await Expect(panel).ToBeVisibleAsync();
        await Expect(panel.Locator(".visitas-detalle-fechas")).ToHaveTextAsync(await fila.Locator("button.nombre-abre-vista-rapida").InnerTextAsync());

        // El lápiz de la cabecera del panel abre el formulario de edición de esa Visita.
        await panel.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Editar la visita", Exact = true }).ClickAsync();
        await Expect(FormularioEdicion(page)).ToBeVisibleAsync();
        await Expect(panel).ToHaveCountAsync(0);
    }

    /// <summary>
    /// El icono 360 de la fila lleva a la página de la Visita sin abrir la vista rápida por el
    /// camino. Con el panel cerrado (excepción 12 de #1163): abierto, tapa la lista.
    /// </summary>
    [Fact]
    public async Task El_icono_360_de_la_fila_lleva_a_la_pagina_sin_abrir_la_vista_rapida()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var (page, fila) = await AbrirConUnaVisitaAsync(contexto);
        await Expect(PanelVisita(page)).ToHaveCountAsync(0);

        await fila.Locator("a.boton-360-pagina").ClickAsync();

        await page.WaitForURLAsync(PaginaVisita);
        await Expect(page.Locator("[data-pieza=cabecera-identidad]")).ToBeVisibleAsync();
        await Expect(PanelVisita(page)).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Enter_abre_la_vista_rapida_de_la_fila_enfocada_y_la_tecla_e_abre_su_edicion()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var (page, fila) = await AbrirConUnaVisitaAsync(contexto);
        var panel = PanelVisita(page);
        var fechas = await fila.Locator("button.nombre-abre-vista-rapida").InnerTextAsync();

        await EnfocarLaFilaConJAsync(page, fila);
        await page.Keyboard.PressAsync("Enter");
        await Expect(panel.Locator(".visitas-detalle-fechas")).ToHaveTextAsync(fechas);

        // Con el panel abierto (un diálogo modal) la tecla «e» no llega a la lista: se cierra y, de
        // vuelta en la lista, «e» sobre la fila enfocada abre el formulario de edición.
        await page.Keyboard.PressAsync("Escape");
        await Expect(panel).ToHaveCountAsync(0);
        await EnfocarLaFilaConJAsync(page, fila);
        await page.Keyboard.PressAsync("e");

        var formulario = FormularioEdicion(page);
        await Expect(formulario).ToBeVisibleAsync();
        await Expect(formulario.GetByLabel("Fecha de inicio", new LocatorGetByLabelOptions { Exact = true }))
            .ToHaveValueAsync(DateOnly.ParseExact(fechas.Trim(), "dd/MM/yyyy").ToString("yyyy-MM-dd"));
    }
}
