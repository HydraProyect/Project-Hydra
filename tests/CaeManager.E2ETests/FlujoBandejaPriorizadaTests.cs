using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Cubre la bandeja priorizada del gestor (/bandeja, "Mi trabajo" en el
/// menú — Horizonte 1.6 de MACRO_PLAN_2026-08-13.md, "bandeja priorizada
/// de la mañana") — sin ningún E2E hasta ahora. Reutiliza el mismo patrón
/// de creación de Cliente→Empresa→Trabajador→Documento de
/// FlujoCriticoTests para producir un ítem real de tipo "Urgente"
/// (documento con vencimiento a 10 días, mismo umbral que ese test ya
/// verificó contra el semáforo), y confirma que la bandeja lo agrega,
/// que el filtro por tipo funciona, que los atajos j/k mueven el foco
/// (mismo AtajosListaTeclado que P3-31), y que la acción de la tarjeta
/// abre el Documento subyacente.
/// </summary>
[Collection("AppCollection")]
public class FlujoBandejaPriorizadaTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Bandeja_agrega_un_documento_urgente_filtra_y_permite_resolverlo()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var razonSocialCliente = $"Bandeja Cliente {sufijo}";
        var razonSocialEmpresa = $"Bandeja Empresa {sufijo}";
        var nombreTrabajador = "Bandeja";
        var apellidosTrabajador = $"E2E {sufijo}";
        var dniTrabajador = Ayudas.GenerarDniValido(88_000_501);

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

        var drawer = page.Locator(".drawer-panel");

        // --- Preparación: Cliente → Empresa → Trabajador → Documento a 10 días (Urgente) ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");
        await page.GetByText("+ Nuevo cliente").First.ClickAsync();
        await drawer.GetByLabel("Razón social").FillAsync(razonSocialCliente);
        await drawer.GetByLabel("CIF", new LocatorGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_996_601));
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/empresas");
        await page.GetByText("+ Nueva empresa").First.ClickAsync();
        await drawer.GetByLabel("Razón social").FillAsync(razonSocialEmpresa);
        await drawer.GetByLabel("CIF", new LocatorGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_996_602));
        await drawer.GetByPlaceholder("Buscar…").FillAsync(razonSocialCliente);
        await drawer.GetByLabel(razonSocialCliente).CheckAsync();
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
        await page.GetByText("+ Nuevo trabajador").First.ClickAsync();
        // A diferencia de FlujoCriticoTests (tenant con una única Empresa en
        // ese momento), en esta colección compartida el tenant ya puede
        // tener varias Empresas de otros tests -- DDL-076 (resolución
        // silenciosa) solo aplica con una sola, así que hay que comprobar
        // visibilidad real, no solo si el <select> existe en el DOM (existe
        // oculto incluso cuando se resuelve en silencio) ni comprobarlo
        // antes de que Blazor termine de decidir qué rama pintar.
        var comboEmpresa = drawer.GetByRole(AriaRole.Combobox, new LocatorGetByRoleOptions { Name = "Empresa" });
        await page.WaitForTimeoutAsync(300);
        if (await comboEmpresa.IsVisibleAsync())
            await comboEmpresa.SelectOptionAsync(new SelectOptionValue { Label = razonSocialEmpresa });
        else
            await drawer.GetByText(razonSocialEmpresa).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await drawer.GetByLabel("Documento de identidad (DNI, NIE, TIE o pasaporte)").FillAsync(dniTrabajador);
        await drawer.GetByLabel("Nombre", new LocatorGetByLabelOptions { Exact = true }).FillAsync(nombreTrabajador);
        await drawer.GetByLabel("Apellidos").FillAsync(apellidosTrabajador);
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos");
        await page.GetByText("+ Nuevo documento").First.ClickAsync();
        await drawer.GetByLabel("Trabajador", new LocatorGetByLabelOptions { Exact = true })
            .FillAsync($"{nombreTrabajador} {apellidosTrabajador} ({dniTrabajador})");
        await page.WaitForTimeoutAsync(500);
        await drawer.GetByLabel("Tipo de documento").SelectOptionAsync(new SelectOptionValue { Label = "Formación 60h (base convenio)" });

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        await drawer.GetByLabel("Fecha de emisión", new LocatorGetByLabelOptions { Exact = true }).FillAsync(hoy.ToString("yyyy-MM-dd"));
        await drawer.GetByLabel("Fecha de vencimiento").FillAsync(hoy.AddDays(10).ToString("yyyy-MM-dd"));

        var rutaPdf = Ayudas.GenerarPdfDePruebaEnDisco();
        try
        {
            await drawer.Locator("input[type=\"file\"]").SetInputFilesAsync(rutaPdf);
            await drawer.GetByText("Archivo adjuntado correctamente.").WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
            await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
        }
        finally
        {
            File.Delete(rutaPdf);
        }

        // --- Bandeja: el documento aparece como "Urgente" ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/bandeja");

        var tarjeta = page.Locator(".panel-resolver-item", new PageLocatorOptions { HasText = apellidosTrabajador });
        await tarjeta.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // Filtra por "Urgente" — la tarjeta debe seguir visible; filtrar por
        // otro tipo (Vencido) debe ocultarla, confirmando que el filtro de
        // verdad reduce la lista y no solo decora. Por Value, no por Label:
        // Bandeja.razor añade el contador a cada <option> ("Urgente (1)"),
        // así que el texto visible no es estable entre ejecuciones, pero el
        // value (nameof(TipoItemBandeja.X)) sí lo es.
        await page.GetByLabel("Tipo").SelectOptionAsync(new SelectOptionValue { Value = "Urgente" });
        await tarjeta.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        await page.GetByLabel("Tipo").SelectOptionAsync(new SelectOptionValue { Value = "Vencido" });
        await tarjeta.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });

        await page.GetByLabel("Tipo").SelectOptionAsync(new SelectOptionValue { Value = "Urgente" });
        await tarjeta.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        // --- Atajos de teclado: con un único ítem filtrado, "j" lo enfoca ---
        // Tab, no clic en el h1: un encabezado no es focable, así que un
        // clic ahí no mueve el foco de verdad (atajos-lista.js ignora
        // j/k/x/Enter mientras el foco sigue en un <input>/<textarea>).
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("j");
        await Expect(tarjeta).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("panel-resolver-item-enfocado"));

        // --- Resolver: la acción de la tarjeta abre el Documento subyacente ---
        await tarjeta.GetByRole(AriaRole.Button).ClickAsync();
        var workspacePanel = page.Locator(".workspace-panel");
        await workspacePanel.GetByText(apellidosTrabajador).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
