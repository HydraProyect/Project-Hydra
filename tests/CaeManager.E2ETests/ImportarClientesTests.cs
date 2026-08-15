using ClosedXML.Excel;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Cubre /clientes/importar (ImportarClientes.razor) — sin ningún E2E hasta
/// ahora, dentro del bloque de importación masiva más prioritario de la
/// auditoría de cobertura (importar datos mal a granel es mucho peor que un
/// alta manual mala). Analiza primero sin escribir nada
/// (AnalizarPlantillaClientesQuery) y solo confirma al pulsar "Confirmar
/// importación" (EjecutarImportacionCommand).
///
/// Este test documenta, con una fila real, un comportamiento verificado
/// leyendo EjecutarImportacionCommandHandler: desde que Cliente exige CIF y
/// Centro exige Empresa (Fase 10), esta plantilla de una sola columna
/// (Cliente/Centro, sin CIF ni Empresa) ya NO puede dar de alta un Cliente o
/// Centro nuevo — el comentario del propio handler lo dice ("ninguno de los
/// dos formatos de Excel soportados hoy recoge esos datos todavía"). El
/// paso 2 (el plan) sigue contando la fila como "Cliente nuevo"/"Centro
/// nuevo" porque el análisis solo compara nombres contra la base de datos;
/// al confirmar, esa misma fila termina siempre en Omitidos con 0 altas
/// reales. Es el propio "que no importe basura en silencio" que motiva este
/// bloque de tests: la fila nunca se crea a medias ni se crea con datos
/// inventados, se declara omitida con el motivo exacto.
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
            await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes/importar");

            await page.Locator("input[type=\"file\"]").SetInputFilesAsync(rutaExcel);

            // --- Paso 2: el plan cuenta la fila como Cliente/Centro nuevo ---
            await page.GetByText("2. Revisa el plan de importación").WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Clientes nuevos"));
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Centros nuevos"));
            Assert.Equal("0", await Ayudas.LeerMetricaAsync(page, "Ya existían"));

            await page.GetByText("Confirmar importación").ClickAsync();

            // --- Resultado: la fila "nueva" no crea nada, termina omitida con motivo explícito ---
            await page.GetByText("Importación completada").WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            Assert.Equal("0", await Ayudas.LeerMetricaAsync(page, "Clientes creados"));
            Assert.Equal("0", await Ayudas.LeerMetricaAsync(page, "Centros creados"));

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
}
