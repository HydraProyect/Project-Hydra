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
        // verdad reduce la lista y no solo decora. Chips en vez de <select>
        // desde el rediseño (mockup "Mi trabajo TALVEG"): cada chip es un
        // <button> con su propio nombre accesible ("Urgente (N)"), único por
        // texto exacto entre los ocho tipos — sustituye al SelectOptionAsync
        // por Value que usaba el <select> anterior.
        var chipUrgente = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Urgente", Exact = false });
        var chipVencido = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Vencido", Exact = false });

        await chipUrgente.ClickAsync();
        await tarjeta.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        await chipVencido.ClickAsync();
        await tarjeta.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });

        await chipUrgente.ClickAsync();
        await tarjeta.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        // --- Atajos de teclado ---
        // Nada de Tab: diagnóstico en CI confirmó que, con el <select> de
        // "Tipo" enfocado (tras SelectOptionAsync), un Tab sintético en el
        // Chromium headless de CI no mueve el foco -- se queda en el propio
        // <select> (quirk de Chromium headless con controles nativos).
        // Enfocar directamente el botón de la tarjeta evita depender de Tab.
        //
        // No se asume que sea el único ítem "Urgente": la colección
        // "AppCollection" comparte una única base de datos entre TODAS las
        // clases de test (ver WebAppFixture.cs), y FlujoCriticoTests crea un
        // documento con el mismo vencimiento a 10 días (mismo umbral de
        // "Urgente") -- con ambos tests en la misma suite, ItemsFiltrados
        // tiene 2 elementos, no 1. "j" SÍ enfoca algo desde la primera
        // pulsación (confirmado en CI con diagnóstico servidor: el interop
        // llega bien), pero enfoca el primero de la lista según su orden
        // real, que puede no ser el de este test. Se calcula el índice real
        // de la tarjeta entre las visibles y se pulsa "j" esa cantidad de
        // veces, en vez de asumir una sola pulsación.
        var tarjetasVisibles = page.Locator(".panel-resolver-item");
        var totalVisibles = await tarjetasVisibles.CountAsync();
        var indiceTarjeta = -1;
        for (var i = 0; i < totalVisibles; i++)
        {
            if ((await tarjetasVisibles.Nth(i).InnerTextAsync()).Contains(apellidosTrabajador))
            {
                indiceTarjeta = i;
                break;
            }
        }
        Assert.True(indiceTarjeta >= 0, "No se encontró la tarjeta de este test entre los ítems 'Urgente' filtrados.");

        await tarjeta.GetByRole(AriaRole.Button).FocusAsync();
        for (var i = 0; i <= indiceTarjeta; i++)
        {
            await page.Keyboard.PressAsync("j");
            await page.WaitForTimeoutAsync(200);
        }

        await Expect(tarjeta).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("panel-resolver-item-enfocado"));

        // --- Resolver: la acción de la tarjeta abre el Documento subyacente ---
        // No es un ".workspace-panel": para un ítem "Urgente" (el caso por
        // defecto de AccionesBandeja.AbrirAsync, ver ese archivo),
        // "Gestionar" navega a /documentos?documentoId=... y esa página abre
        // DrawerGestionDocumento -- el mismo ".drawer-panel" que el resto de
        // la app, no un workspace panel (ese es el destino de otros tipos de
        // ítem, como RequisitoPendiente). Al editar un documento existente,
        // el Drawer muestra el nombre del propietario en modo solo lectura
        // (_propietarioNombreSoloLectura, ver DrawerGestionDocumento.razor.cs),
        // que contiene los apellidos del trabajador.
        await tarjeta.GetByRole(AriaRole.Button).ClickAsync();
        var drawerDocumento = page.Locator(".drawer-panel");
        await drawerDocumento.GetByText(apellidosTrabajador).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
