using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

[Collection("AppCollection")]
public class ProyectosFase1SelectorTests(WebAppFixture fixture)
{
    /// <summary>
    /// El Cliente empresarial es un filtro opcional de la lista: se llega con los Proyectos de todos los que
    /// el usuario alcanza, se elige uno con el teclado y «Todos» lo retira sin vaciar la lista ni quitar el alta.
    /// </summary>
    [Fact]
    public async Task El_filtro_de_Cliente_empresarial_se_elige_y_se_retira_con_teclado_real()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl,
            Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/proyectos");
        var selector = page.GetByRole(AriaRole.Button,
            new PageGetByRoleOptions { NameRegex = new Regex(@"^Cliente(?:$|:)") });
        await Expect(selector).ToBeVisibleAsync();
        await Expect(selector).ToHaveTextAsync("Cliente");
        // Sin elegir nada, la lista ya trae filas: las de todos los Clientes empresariales del alcance.
        var filas = page.Locator("table.tabla-proyectos tbody tr");
        await Expect(filas).Not.ToHaveCountAsync(0);
        await selector.FocusAsync();
        await selector.PressAsync("Space");
        var panelId = await selector.GetAttributeAsync("aria-controls");
        Assert.False(string.IsNullOrWhiteSpace(panelId), "Control positivo: selector con panel identificado.");
        var menu = page.Locator("#" + panelId);
        await Expect(menu).ToBeVisibleAsync();
        const string sinEleccion = "Todos";
        var nombres = (await menu.GetByRole(AriaRole.Menuitemradio).AllTextContentsAsync())
            .Select(n => n.Trim()).ToArray();
        Assert.Contains(sinEleccion, nombres);
        var contraparte = nombres.FirstOrDefault(n => n != sinEleccion);
        Assert.False(string.IsNullOrWhiteSpace(contraparte), "Control positivo: el catálogo contiene un Cliente concreto.");
        var opcion = menu.GetByRole(AriaRole.Menuitemradio,
            new LocatorGetByRoleOptions { Name = contraparte, Exact = true });
        await opcion.FocusAsync();
        await opcion.PressAsync("Space");
        await Expect(menu).ToBeHiddenAsync();
        Assert.True((await selector.InnerTextAsync()).Trim() == "Cliente: " + contraparte,
            "La selección física no aplica el Cliente concreto a la pastilla de Proyectos.");
        // Control positivo de la ausencia del final: elegido, el filtro viaja en la URL.
        await Expect(page).ToHaveURLAsync(new Regex("cliente="));
        await Expect(selector).ToBeFocusedAsync();
        await selector.PressAsync("Space");
        await Expect(menu).ToBeVisibleAsync();
        await Expect(menu.GetByRole(AriaRole.Menuitemradio,
            new LocatorGetByRoleOptions { Name = contraparte, Exact = true })).ToBeFocusedAsync();
        var elegir = menu.GetByRole(AriaRole.Menuitemradio,
            new LocatorGetByRoleOptions { Name = sinEleccion, Exact = true });
        await elegir.FocusAsync();
        await elegir.PressAsync("Space");
        await Expect(menu).ToBeHiddenAsync();
        await Expect(selector).ToHaveTextAsync("Cliente");
        await Expect(selector).ToBeFocusedAsync();
        await Expect(page).Not.ToHaveURLAsync(new Regex("cliente="));
        await Expect(filas).Not.ToHaveCountAsync(0);
        // Con filas, el alta solo está en la cabecera (el estado vacío, que lleva otra, no se pinta).
        await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "+ Nuevo proyecto", Exact = true })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Seguir_editando_conserva_el_Cliente_empresarial_y_devuelve_foco_al_selector_recreado()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl,
            Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/proyectos");
        const string sinEleccion = "Todos";
        var selector = SelectorCliente(page);
        var menu = await AbrirMenuClienteAsync(selector, page);
        var opciones = (await menu.GetByRole(AriaRole.Menuitemradio).AllTextContentsAsync())
            .Select(n => n.Trim()).Where(n => n != sinEleccion).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.True(opciones.Length > 0 && opciones.Distinct(StringComparer.Ordinal).Count() == opciones.Length,
            "Control positivo: catálogo no vacío y nombres de Clientes sin duplicados.");
        await selector.PressAsync("Escape");
        await Expect(menu).ToBeHiddenAsync();
        var filas = page.Locator("table.tabla-proyectos tbody tr");
        string? clienteConProyectos = null;
        foreach (var nombre in opciones)
        {
            menu = await AbrirMenuClienteAsync(selector, page);
            var opcion = menu.GetByRole(AriaRole.Menuitemradio,
                new LocatorGetByRoleOptions { Name = nombre, Exact = true });
            await opcion.FocusAsync();
            await opcion.PressAsync("Space");
            await Expect(menu).ToBeHiddenAsync(); // El callback termina después de cargar este contexto.
            await Expect(selector).ToHaveTextAsync("Cliente: " + nombre);
            await Expect(page.GetByRole(AriaRole.Heading,
                new PageGetByRoleOptions { Name = "No pudimos cargar los proyectos", Exact = true })).ToHaveCountAsync(0);
            if (await filas.CountAsync() > 0)
            {
                clienteConProyectos = nombre;
                break;
            }
        }
        Assert.False(string.IsNullOrWhiteSpace(clienteConProyectos),
            "Control positivo: algún Cliente del catálogo tiene proyectos visibles sembrados.");
        var nombresAntes = (await filas.Locator("button.nombre-proyecto").AllTextContentsAsync())
            .Select(n => n.Trim()).ToArray();
        Assert.True(nombresAntes.Length > 0 && nombresAntes.All(n => !string.IsNullOrWhiteSpace(n)),
            "Control positivo: las filas reales tienen nombres de Proyecto.");
        var nombreProyecto = nombresAntes[0];
        var filaProyecto = filas.Filter(new LocatorFilterOptions
        { Has = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = nombreProyecto, Exact = true }) });
        await Expect(filaProyecto).ToHaveCountAsync(1);
        await filaProyecto.GetByRole(AriaRole.Button,
            new LocatorGetByRoleOptions { Name = nombreProyecto, Exact = true }).ClickAsync();
        var panel = page.Locator("aside.panel-proyecto");
        await Expect(panel.Locator(".nombre-cabecera-panel-proyecto")).ToHaveTextAsync(nombreProyecto);
        await panel.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Editar la información del proyecto", Exact = true }).ClickAsync();
        var campoNombre = panel.GetByRole(AriaRole.Textbox, new LocatorGetByRoleOptions { Name = "Nombre", Exact = true });
        await Expect(campoNombre).ToHaveValueAsync(nombreProyecto);
        var nombreSinGuardar = nombreProyecto + " — edición sin guardar";
        await campoNombre.FillAsync(nombreSinGuardar);
        menu = await AbrirMenuClienteAsync(selector, page); // El foco sale del input y notifica su cambio.
        var panelAnteriorId = await selector.GetAttributeAsync("aria-controls");
        var elegir = menu.GetByRole(AriaRole.Menuitemradio,
            new LocatorGetByRoleOptions { Name = sinEleccion, Exact = true });
        await elegir.FocusAsync();
        await elegir.PressAsync("Space");
        var pregunta = page.GetByRole(AriaRole.Dialog).Filter(new LocatorFilterOptions
        { Has = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Seguir editando", Exact = true }) });
        await Expect(pregunta).ToBeVisibleAsync(); // Cambio notificado: no se acepta un setup sin edición pendiente.
        var seguir = pregunta.GetByRole(AriaRole.Button,
            new LocatorGetByRoleOptions { Name = "Seguir editando", Exact = true });
        await seguir.FocusAsync();
        await seguir.PressAsync("Space");
        await Expect(pregunta).ToBeHiddenAsync();
        await Expect(selector).Not.ToHaveAttributeAsync("aria-controls", panelAnteriorId!);
        await Expect(selector).ToBeVisibleAsync();
        await Expect(selector).ToHaveTextAsync("Cliente: " + clienteConProyectos);
        await Expect(panel.Locator(".nombre-cabecera-panel-proyecto")).ToHaveTextAsync(nombreProyecto);
        await Expect(campoNombre).ToHaveValueAsync(nombreSinGuardar);
        Assert.Equal(nombresAntes, (await filas.Locator("button.nombre-proyecto").AllTextContentsAsync())
            .Select(n => n.Trim()).ToArray());
        Assert.Equal(1, await selector.CountAsync());
        Assert.True(await selector.EvaluateAsync<bool>("elemento => elemento === document.activeElement"),
            "Foco del selector de Cliente ausente tras Seguir editando y recrear la pastilla.");
        // Se cierra el contexto sin Guardar: no se mide persistencia ni orden SQL.
    }

    private static ILocator SelectorCliente(IPage page) => page.GetByRole(AriaRole.Button,
        new PageGetByRoleOptions { NameRegex = new Regex(@"^Cliente(?:$|:)") });

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
}
