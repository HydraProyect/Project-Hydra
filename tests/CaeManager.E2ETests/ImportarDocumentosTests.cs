using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Cubre la plantilla de Documentos del asistente unificado (/importacion,
/// Importacion.razor — Plantillas["documentos"]), alcanzada desde
/// /documentos/importar, que desde H-1 (2026-09-02) redirige ahí en vez de
/// renderizar su propia página (ImportarDocumentos.razor pasó a ser un stub
/// de redirección — el hub absorbe las migraciones tabulares, 0 enlaces
/// entrantes propios). A diferencia de las plantillas de Clientes, esta
/// nunca da de alta Trabajador ni TipoDocumento: ambos deben existir ya (ver
/// ClosedXmlPlantillaDocumentosService) — de ahí que el test empiece
/// creando un Cliente → Empresa → Trabajador reales por UI (mismo
/// encadenado que FlujoCriticoTests) antes de poder subir la plantilla.
///
/// La plantilla lleva dos filas: una válida (el DNI del Trabajador recién
/// creado, con "Certificado de aptitud médica" — TipoDocumento sembrado — y una fecha
/// de emisión pasada) y una deliberada con un DNI que no existe en el
/// sistema. Es el caso "referencia a una entidad inexistente" que este
/// bloque de tests debe demostrar que la app detecta: la fila con el DNI
/// inventado no cuenta como documento nuevo en el plan (aparece en Omitidos
/// desde el paso 2, análisis-time), no crea ningún Documento.
/// </summary>
[Collection("AppCollection")]
public class ImportarDocumentosTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Importar_documentos_crea_el_documento_valido_y_omite_el_DNI_inexistente()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var razonSocialCliente = $"ImportarDocs Cliente {sufijo}";
        var razonSocialEmpresa = $"ImportarDocs Empresa {sufijo}";
        var nombreTrabajador = "Trabajador";
        var apellidosTrabajador = $"ImportarDocs {sufijo}";
        var dniTrabajador = Ayudas.GenerarDniValido(88_200_001);
        var dniInexistente = Ayudas.GenerarDniValido(88_200_002);
        var fechaEmision = DateTime.UtcNow.Date.AddDays(-30);

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        var drawer = page.Locator(".drawer-panel");

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

        // --- Preparación: Cliente → Empresa → Trabajador reales, mismo patrón que FlujoCriticoTests ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");
        await page.GetByText("+ Nuevo cliente").First.ClickAsync();
        await drawer.GetByLabel("Razón social").FillAsync(razonSocialCliente);
        await drawer.GetByLabel("CIF", new LocatorGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_995_101));
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/empresas");
        await page.GetByText("+ Nueva empresa").First.ClickAsync();
        await drawer.GetByLabel("Razón social").FillAsync(razonSocialEmpresa);
        await drawer.GetByLabel("CIF", new LocatorGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_995_102));
        await drawer.GetByPlaceholder("Buscar…").FillAsync(razonSocialCliente);
        await drawer.GetByLabel(razonSocialCliente).CheckAsync();
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
        await page.GetByText("+ Nuevo trabajador").First.ClickAsync();
        await Ayudas.SeleccionarEmpresaEnDrawerTrabajadorAsync(drawer, razonSocialEmpresa);
        await drawer.GetByLabel("Documento de identidad (DNI, NIE, TIE o pasaporte)").FillAsync(dniTrabajador);
        await drawer.GetByLabel("Nombre", new LocatorGetByLabelOptions { Exact = true }).FillAsync(nombreTrabajador);
        await drawer.GetByLabel("Apellidos").FillAsync(apellidosTrabajador);
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Plantilla de Documentos: una fila válida, una con DNI inexistente ---
        var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add("Documentos");
        hoja.Cell(1, 1).Value = "DNI";
        hoja.Cell(1, 2).Value = "Tipo de documento";
        hoja.Cell(1, 3).Value = "Fecha de emisión";
        hoja.Cell(2, 1).Value = dniTrabajador;
        hoja.Cell(2, 2).Value = "Certificado de aptitud médica";
        hoja.Cell(2, 3).Value = fechaEmision;
        hoja.Cell(3, 1).Value = dniInexistente;
        hoja.Cell(3, 2).Value = "Certificado de aptitud médica";
        hoja.Cell(3, 3).Value = fechaEmision;
        var rutaExcel = Ayudas.GuardarLibroDePruebaEnDisco(libro, "importar-documentos.xlsx");

        try
        {
            // /documentos/importar redirige aquí con la plantilla ya preseleccionada (H-1).
            await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos/importar");
            await Expect(page).ToHaveURLAsync(new Regex(@"/importacion\?plantilla=documentos$"));

            await page.GetByText("Continuar con Documentos").ClickAsync();
            await page.Locator("input[type=\"file\"]").SetInputFilesAsync(rutaExcel);

            // --- Paso 2: una fila nueva, la del DNI inexistente ya aparece omitida ---
            var botonVerPlan = page.GetByText("Ver plan de importación");
            await Expect(botonVerPlan).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 15_000 });
            await botonVerPlan.ClickAsync();

            // GetByText("Revisar plan") a secas es ambigua: el nombre del paso
            // 3 aparece tanto en el botón del stepper del wizard como en el <h2>
            // de esta sección — el rol Heading es inequívoco.
            await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Revisar plan" })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            await Expect(page.GetByText("1 se crearán")).ToBeVisibleAsync();
            await Expect(page.GetByText("1 se omitirán")).ToBeVisibleAsync();

            var filaOmitidaEnPlan = page.Locator(".tabla-plan-importacion-envoltorio .tabla-datos tbody tr", new PageLocatorOptions { HasText = dniInexistente });
            await filaOmitidaEnPlan.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
            await Expect(filaOmitidaEnPlan).ToContainTextAsync("No existe ningún trabajador con este DNI");

            await page.GetByText("Continuar a confirmar").ClickAsync();
            await page.GetByText("He revisado el plan y quiero escribir estos datos").ClickAsync();
            await page.GetByText("Importar ahora").ClickAsync();

            // --- Resultado: 1 documento real creado, el DNI inexistente sigue omitido ---
            await page.Locator(".titulo-reporte-importacion")
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Creados"));
            Assert.Equal("0", await Ayudas.LeerMetricaAsync(page, "Avisos"));
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Omitidos"));
            await page.Locator(".tabla-datos tbody tr", new PageLocatorOptions { HasText = dniInexistente })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        }
        finally
        {
            File.Delete(rutaExcel);
        }

        // --- Verificación real: el Documento existe en la ficha del Trabajador ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos");
        await page.GetByPlaceholder("Buscar por propietario o tipo de documento…").FillAsync(apellidosTrabajador);
        var fila = page.Locator("tr", new PageLocatorOptions { HasText = apellidosTrabajador });
        await fila.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await Expect(fila).ToContainTextAsync("Certificado de aptitud médica");
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
    private static IPageAssertions Expect(IPage page) => Assertions.Expect(page);
}
