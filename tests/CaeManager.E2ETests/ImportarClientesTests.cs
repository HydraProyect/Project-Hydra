using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Cubre la plantilla de Clientes del asistente unificado (/importacion,
/// Importacion.razor — Plantillas["clientes"]), alcanzada desde
/// /clientes/importar, que desde H-1 (2026-09-02) redirige ahí en vez de
/// renderizar su propia página (ImportarClientes.razor pasó a ser un stub de
/// redirección — el hub absorbe las migraciones tabulares, 0 enlaces
/// entrantes propios). Analiza primero sin escribir nada
/// (AnalizarPlantillaClientesQuery) y solo confirma al pulsar "Importar
/// ahora" (EjecutarImportacionCommand) — misma query/comando que usaba la
/// página retirada, así que el comportamiento de dominio no cambia.
///
/// Este test documenta, con una fila real, un comportamiento verificado
/// leyendo EjecutarImportacionCommandHandler: desde que Cliente exige CIF y
/// Centro exige Empresa (Fase 10), esta plantilla de una sola columna
/// (Cliente/Centro, sin CIF ni Empresa) ya NO puede dar de alta un Cliente o
/// Centro nuevo — el comentario del propio handler lo dice ("ninguno de los
/// dos formatos de Excel soportados hoy recoge esos datos todavía"). El
/// paso 2 (el plan) sigue contando la fila como dos altas potenciales
/// (Cliente + Centro) porque el análisis solo compara nombres contra la base
/// de datos; al confirmar, la fila nunca se crea a medias ni con datos
/// inventados: se omite con el motivo exacto, y el bucle del handler corta
/// (`continue`) en cuanto falta el Cliente, así que es un único Omitido, no
/// dos.
/// </summary>
[Collection("AppCollection")]
public class ImportarClientesTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Importar_clientes_analiza_el_plan_pero_una_fila_nueva_termina_omitida_sin_CIF()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var nombreClienteCentro = $"ImportarClientes {sufijo}";

        var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add("Clientes");
        hoja.Cell(1, 1).Value = "Cliente / Centro";
        hoja.Cell(1, 2).Value = "Crítico (C/N)";
        hoja.Cell(1, 3).Value = "Dirección";
        hoja.Cell(1, 4).Value = "Contacto";
        hoja.Cell(2, 1).Value = nombreClienteCentro;
        hoja.Cell(2, 2).Value = "N";
        hoja.Cell(2, 3).Value = "Calle de Prueba 1";
        hoja.Cell(2, 4).Value = "Persona de Contacto — contacto@ejemplo.test";
        var rutaExcel = Ayudas.GuardarLibroDePruebaEnDisco(libro, "importar-clientes.xlsx");

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        try
        {
            await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
            // /clientes/importar redirige aquí con la plantilla ya preseleccionada (H-1).
            await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes/importar");
            await Expect(page).ToHaveURLAsync(new Regex(@"/importacion\?plantilla=clientes$"));

            await page.GetByText("Continuar con Plantilla de Clientes").ClickAsync();
            await page.Locator("input[type=\"file\"]").SetInputFilesAsync(rutaExcel);

            // --- Paso 2 "Revisar plan": cuenta la fila como dos altas potenciales (Cliente + Centro) ---
            var botonVerPlan = page.GetByText("Ver plan de importación");
            await Expect(botonVerPlan).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 15_000 });
            await botonVerPlan.ClickAsync();

            // GetByText("Revisar plan") a secas es ambigua: el nombre del paso
            // 3 aparece tanto en el botón del stepper del wizard como en el <h2>
            // de esta sección — el rol Heading es inequívoco.
            await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Revisar plan" })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            await Expect(page.GetByText("2 se crearán")).ToBeVisibleAsync();
            await Expect(page.GetByText("0 se omitirán")).ToBeVisibleAsync();

            await page.GetByText("Continuar a confirmar").ClickAsync();
            await page.GetByText("He revisado el plan y quiero escribir estos datos").ClickAsync();
            await page.GetByText("Importar ahora").ClickAsync();

            // --- Resultado: la fila "nueva" no crea nada, termina omitida con motivo explícito ---
            await page.Locator(".titulo-reporte-importacion")
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            Assert.Equal("0", await Ayudas.LeerMetricaAsync(page, "Creados"));
            Assert.Equal("0", await Ayudas.LeerMetricaAsync(page, "Avisos"));
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Omitidos"));

            var filaOmitida = page.Locator(".tabla-datos tbody tr", new PageLocatorOptions { HasText = nombreClienteCentro });
            await filaOmitida.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
            await Expect(filaOmitida).ToContainTextAsync("CIF");

            // --- Verificación real: nunca se creó ningún Cliente con este nombre ---
            await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");
            await page.GetByPlaceholder("Buscar por nombre…").FillAsync(nombreClienteCentro);
            await page.WaitForTimeoutAsync(500); // debounce de CampoTexto
            await Expect(page.Locator("tr", new PageLocatorOptions { HasText = nombreClienteCentro })).ToHaveCountAsync(0);
        }
        finally
        {
            File.Delete(rutaExcel);
        }
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
    private static IPageAssertions Expect(IPage page) => Assertions.Expect(page);
}
