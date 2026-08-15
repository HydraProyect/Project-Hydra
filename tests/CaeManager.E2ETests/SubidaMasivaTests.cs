using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Cubre /documentos/subida-masiva (SubidaMasiva.razor) — sin ningún E2E
/// hasta ahora. A diferencia de las otras 4 pantallas de este bloque, esta
/// no sube un Excel: arrastra archivos (PDF/imagen/Word/.zip) y detecta
/// Trabajador + TipoDocumento por archivo (DetectarCamposDocumentoQuery). Si
/// la confianza es alta (>=95) se crea directo; si no, queda "Pendiente de
/// confirmar" con lo detectado prellenado.
///
/// DeteccionPreviaDocumentoOptions.Activa es false por defecto (mismo
/// criterio "inerte por defecto" que el resto de integraciones de IA — ver
/// DetectarCamposDocumentoQueryHandler) y WebAppFixture no la activa, así
/// que en este entorno de test la detección devuelve confianza 0 siempre:
/// el archivo subido termina "Pendiente de confirmar" de forma
/// determinista, sin depender de que una IA real acierte. El test cubre por
/// tanto la rama de confirmación manual, que es la única alcanzable aquí.
///
/// SubidaMasiva.razor no declara [Authorize(Roles=...)] — a diferencia de
/// "Importar documentos" (Administrador-only, envuelto en AuthorizeView en
/// Documentos.razor), el enlace "Subida múltiple" se muestra a cualquier
/// rol autenticado y CrearDocumentoCommandHandler tampoco comprueba rol. El
/// segundo test de esta clase confirma con un usuario real que no es
/// Administrador que la pantalla es alcanzable — no es una suposición leída
/// del código, es el comportamiento real.
/// </summary>
[Collection("AppCollection")]
public class SubidaMasivaTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Subida_multiple_deja_el_archivo_pendiente_de_confirmar_y_crea_el_Documento_al_confirmar()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var razonSocialCliente = $"SubidaMasiva Cliente {sufijo}";
        var razonSocialEmpresa = $"SubidaMasiva Empresa {sufijo}";
        var nombreTrabajador = "Trabajador";
        var apellidosTrabajador = $"SubidaMasiva {sufijo}";
        var dniTrabajador = Ayudas.GenerarDniValido(88_300_001);

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        var drawer = page.Locator(".drawer-panel");

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

        // --- Preparación: Cliente → Empresa → Trabajador reales, mismo patrón que FlujoCriticoTests ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");
        await page.GetByText("+ Nuevo cliente").First.ClickAsync();
        await drawer.GetByLabel("Razón social").FillAsync(razonSocialCliente);
        await drawer.GetByLabel("CIF", new LocatorGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_994_101));
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/empresas");
        await page.GetByText("+ Nueva empresa").First.ClickAsync();
        await drawer.GetByLabel("Razón social").FillAsync(razonSocialEmpresa);
        await drawer.GetByLabel("CIF", new LocatorGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_994_102));
        await drawer.GetByPlaceholder("Buscar…").FillAsync(razonSocialCliente);
        await drawer.GetByLabel(razonSocialCliente).CheckAsync();
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
        await page.GetByText("+ Nuevo trabajador").First.ClickAsync();
        var comboEmpresa = drawer.GetByRole(AriaRole.Combobox, new LocatorGetByRoleOptions { Name = "Empresa" });
        if (await comboEmpresa.CountAsync() > 0)
        {
            await comboEmpresa.SelectOptionAsync(new SelectOptionValue { Label = razonSocialEmpresa });
        }
        else
        {
            // DDL-076: perfil Cliente Directo con una única Empresa resuelve en
            // silencio con un CampoInfo de solo lectura (ver Trabajadores.razor,
            // _resolverEmpresaEnSilencio) — drawer.GetByText a secas también
            // coincide con el <option> (oculto) del combobox que sigue existiendo
            // en el DOM para otro caso, así que aquí se acota al contenedor real
            // de CampoInfo (.campo-info-valor) para no esperar por un nodo oculto.
            await drawer.Locator(".campo-info-valor", new LocatorLocatorOptions { HasText = razonSocialEmpresa })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        }
        await drawer.GetByLabel("Documento de identidad (DNI, NIE, TIE o pasaporte)").FillAsync(dniTrabajador);
        await drawer.GetByLabel("Nombre", new LocatorGetByLabelOptions { Exact = true }).FillAsync(nombreTrabajador);
        await drawer.GetByLabel("Apellidos").FillAsync(apellidosTrabajador);
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Subida múltiple: un único PDF, sin detección IA activa en el entorno de test ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos/subida-masiva");

        var rutaPdf = Ayudas.GenerarPdfDePruebaEnDisco();
        try
        {
            await page.Locator(".zona-soltar-archivo-input").SetInputFilesAsync(rutaPdf);

            var item = page.Locator(".item-subida-masiva");
            await item.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            await Expect(item).ToContainTextAsync("Pendiente de confirmar", new LocatorAssertionsToContainTextOptions { Timeout = 15_000 });
            await Expect(page.Locator(".resumen-subida-masiva")).ToContainTextAsync("1 pendiente(s)");

            await item.GetByText("Confirmar…").ClickAsync();

            var confirmacion = item.Locator(".item-subida-masiva-confirmacion");
            await confirmacion.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

            await confirmacion.GetByLabel("Trabajador", new LocatorGetByLabelOptions { Exact = true })
                .FillAsync($"{nombreTrabajador} {apellidosTrabajador} ({dniTrabajador})");
            await page.WaitForTimeoutAsync(500);

            await confirmacion.GetByLabel("Tipo de documento").SelectOptionAsync(new SelectOptionValue { Label = "Apto médico laboral" });
            await confirmacion.GetByLabel("Fecha de emisión").FillAsync(DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"));

            await confirmacion.GetByText("Confirmar y crear").ClickAsync();

            await Expect(item).ToContainTextAsync("Creado", new LocatorAssertionsToContainTextOptions { Timeout = 15_000 });
        }
        finally
        {
            File.Delete(rutaPdf);
        }

        // --- Verificación real: el Documento existe en /documentos ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos");
        await page.GetByPlaceholder("Buscar por propietario o tipo de documento…").FillAsync(apellidosTrabajador);
        var fila = page.Locator("tr", new PageLocatorOptions { HasText = apellidosTrabajador });
        await fila.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await Expect(fila).ToContainTextAsync("Apto médico laboral");
    }

    /// <summary>
    /// SubidaMasiva.razor no restringe por rol (a diferencia de "Importar
    /// documentos", Administrador-only) — un usuario Cliente, el rol de
    /// menor alcance de los 6 (ver Roles.cs), llega igualmente a la
    /// pantalla y no un /acceso-denegado. No afirma que esto esté mal (puede
    /// ser deliberado: autoservicio de subida de sus propios documentos),
    /// solo dónde está la frontera real hoy — dato que la auditoría de
    /// cobertura pidió confirmar, no asumir.
    /// </summary>
    [Fact]
    public async Task Subida_multiple_es_alcanzable_por_un_rol_que_no_es_Administrador()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("cliente", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos/subida-masiva");

        Assert.DoesNotContain("/acceso-denegado", page.Url);
        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Subida múltiple de documentos" })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
