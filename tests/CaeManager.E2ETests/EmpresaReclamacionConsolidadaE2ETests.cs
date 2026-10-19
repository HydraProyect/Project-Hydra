using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Empresa 360 · pestaña «Reclamación» (decisión de Chris, 2026-10-19): lo pendiente de la Empresa empleadora y de sus
/// Trabajadores, consolidado en tarjetas; «Reclamar» abre el cajón con los destinatarios de la agenda de la Empresa.
///
/// El recorrido monta el escenario por la interfaz (Tipo de documento → Empresa/Cliente/Centro → Trabajador con un Documento
/// que vence en 10 días → Asignación al Centro → contacto en la agenda de la Empresa) y termina en el cajón ya preparado.
/// NO pulsa «Enviar»: el entorno E2E no tiene SMTP ni buzón y, aunque lo tuviera, un test no debe mandar correo; el envío lo
/// cubren los de integración (ReclamacionConsolidadaEmpresaEmpleadoraTests) y los bUnit de PestanaReclamacionEmpresa.
/// </summary>
[Collection("AppCollection")]
public class EmpresaReclamacionConsolidadaE2ETests(WebAppFixture fixture)
{
    [Fact]
    public async Task La_pestana_Reclamacion_de_Empresa_360_muestra_la_tarjeta_y_Reclamar_abre_el_cajon_con_el_contacto_de_la_agenda()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var nombreTipoDocumento = $"ReclamacionEmpresa Tipo {sufijo}";
        var razonSocialCliente = $"ReclamacionEmpresa Cliente {sufijo}";
        var razonSocialEmpresa = $"ReclamacionEmpresa Empresa {sufijo}";
        var nombreCentro = $"ReclamacionEmpresa Centro {sufijo}";
        var nombreTrabajador = "ReclamacionEmpresa";
        var apellidosTrabajador = $"E2E {sufijo}";
        var dniTrabajador = Ayudas.GenerarDniValido(88_000_911);
        var nombreContacto = $"Contacto ReclamacionEmpresa {sufijo}";
        var emailContacto = $"contacto.reclamacionempresa.{sufijo}@e2e.test";

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 900);

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

        var drawer = page.Locator(".drawer-panel");

        // --- Tipo de documento propio ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/tipos-documento");
        await page.GetByText("+ Nuevo tipo").ClickAsync();
        await drawer.GetByLabel("Nombre").FillAsync(nombreTipoDocumento);
        await drawer.GetByLabel("Orden").FillAsync("0");
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
        await page.Locator("tr", new PageLocatorOptions { HasText = nombreTipoDocumento })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // --- Empresa → Cliente → Centro (alta guiada) ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes/alta-guiada");
        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "1. Empresa" }).WaitForAsync();
        await page.GetByLabel("Razón social").FillAsync(razonSocialEmpresa);
        await page.GetByLabel("Identificación fiscal (opcional)", new PageGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_995_512));
        await page.GetByText("Guardar y continuar").ClickAsync();

        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "2. Cliente" }).WaitForAsync();
        await page.GetByLabel("Razón social").FillAsync(razonSocialCliente);
        await page.GetByLabel("Identificación fiscal", new PageGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_995_511));
        await page.GetByText("Guardar y continuar a Centro").ClickAsync();

        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "3. Centro" }).WaitForAsync();
        await page.GetByLabel("Nombre", new PageGetByLabelOptions { Exact = true }).FillAsync(nombreCentro);
        await page.GetByText("Guardar centro").ClickAsync();
        await Assertions.Expect(page.GetByText("Terminar aquí"))
            .Not.ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // --- Trabajador de esa Empresa ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
        await page.GetByText("+ Nuevo trabajador").First.ClickAsync();
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

        // --- Documento del Trabajador que vence en 10 días ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos");
        await page.GetByText("+ Nuevo documento").First.ClickAsync();
        await drawer.GetByLabel("Trabajador", new LocatorGetByLabelOptions { Exact = true })
            .FillAsync($"{nombreTrabajador} {apellidosTrabajador} ({razonSocialEmpresa})");
        await page.WaitForTimeoutAsync(500);
        await drawer.GetByLabel("Tipo de documento").SelectOptionAsync(new SelectOptionValue { Label = nombreTipoDocumento });
        var hoy = Ayudas.HoyDeNegocio();
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

        // --- Asignación del Trabajador al Centro (sin ella el lote no entra en la reclamación) ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
        await page.GetByPlaceholder("Filtrar esta pantalla: nombre, DNI o alias").FillAsync(apellidosTrabajador);
        var filaTrabajador = page.Locator(".marco-lista-trabajadores .tarjeta-fila-acordeon", new PageLocatorOptions { HasText = apellidosTrabajador });
        await filaTrabajador.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await Assertions.Expect(page.Locator(".marco-lista-trabajadores .fila-pulsable"))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 15_000 });
        // Quedan recargas de la lista en vuelo tras el alta del Trabajador y el filtro; al aterrizar limpian la selección.
        // Se espera a que el DOM esté quieto 1,5 s (condición, no reloj) antes de seleccionar (patrón de CentrosGestionarEnVivoE2ETests).
        await page.WaitForFunctionAsync("""
            () => new Promise(resolver => {
                let temporizador;
                const observador = new MutationObserver(() => { clearTimeout(temporizador); temporizador = setTimeout(fin, 1500); });
                const fin = () => { observador.disconnect(); resolver(true); };
                observador.observe(document.body, { subtree: true, childList: true, attributes: true, characterData: true });
                temporizador = setTimeout(fin, 1500);
            })
            """, null, new PageWaitForFunctionOptions { Timeout = 30_000 });
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Selección múltiple", Exact = true }).ClickAsync();
        for (var intento = 1; ; intento++)
        {
            var casilla = filaTrabajador.Locator("input[type=\"checkbox\"]");
            if (!await casilla.IsCheckedAsync()) await casilla.CheckAsync();
            try
            {
                await page.Locator(".barra-acciones-lote").GetByText("Asignar a centro…")
                    .ClickAsync(new LocatorClickOptions { Timeout = 3_000 });
                break;
            }
            catch (TimeoutException) when (intento < 3) { }
        }
        await page.Locator(".modal-cuerpo").GetByLabel("Centro", new LocatorGetByLabelOptions { Exact = true })
            .FillAsync($"{nombreCentro} ({razonSocialCliente})");
        await page.WaitForTimeoutAsync(500);
        await page.Locator(".modal-pie").GetByText(new Regex("^Asignar")).ClickAsync();
        await page.Locator(".modal-pie").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Empresa 360: se llega por el icono 360 de su fila ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/empresas");
        await page.GetByPlaceholder("Filtrar esta pantalla: razón social o CIF").FillAsync(razonSocialEmpresa);
        var filaEmpresa = page.Locator(".marco-lista-empresas .fila-pulsable", new PageLocatorOptions { HasText = razonSocialEmpresa });
        await Assertions.Expect(page.Locator(".marco-lista-empresas .fila-pulsable"))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 15_000 });
        await filaEmpresa.Locator("a.boton-360-pagina").ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/empresas/[0-9a-f-]{36}$"));
        var urlEmpresa = page.Url;

        // --- Contacto en la agenda de la Empresa (de ahí salen los destinatarios de la reclamación) ---
        await Ayudas.NavegarYEsperarAsync(page, $"{urlEmpresa}?pestana=agenda");
        await page.GetByText("Añadir contacto").First.ClickAsync();
        await page.Locator(".modal-cuerpo").GetByLabel("Nombre").FillAsync(nombreContacto);
        await page.Locator(".modal-cuerpo").GetByLabel("Email").FillAsync(emailContacto);
        await page.Locator(".modal-pie").GetByText("Guardar").ClickAsync();
        await page.GetByText(emailContacto).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // --- La pestaña Reclamación: una tarjeta con el Documento del Trabajador ---
        await Ayudas.NavegarYEsperarAsync(page, $"{urlEmpresa}?pestana=reclamacion");
        var seccion = page.GetByTestId("reclamacion-preventiva");
        var tablaLote = seccion.GetByTestId("reclamacion-lote");
        await tablaLote.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 20_000 });
        await Assertions.Expect(tablaLote.First).ToContainTextAsync(apellidosTrabajador);
        await Assertions.Expect(seccion.GetByTestId("reclamar-todo")).ToBeVisibleAsync();

        // --- Reclamar: el cajón lleva la tarjeta del contacto de la agenda y nada se envía ---
        await seccion.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Reclamar", Exact = true }).First.ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { Timeout = 20_000 });
        await Assertions.Expect(drawer).ToContainTextAsync(emailContacto, new LocatorAssertionsToContainTextOptions { Timeout = 20_000 });
        await Assertions.Expect(drawer).ToContainTextAsync(apellidosTrabajador);

        // Sin clic en «Enviar»: ningún correo sale y la Empresa sigue sin reclamación registrada.
        await Assertions.Expect(page.Locator(".toast.toast-error")).ToHaveCountAsync(0);
    }
}
