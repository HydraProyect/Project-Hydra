using ClosedXML.Excel;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Cubre /importacion (Importacion.razor, el importador del "Cuadro de
/// Control CAE") — sin ningún E2E hasta ahora, el más complejo de las 5
/// pantallas de este bloque (4 hojas heterogéneas: Centros_Plataformas,
/// Empleados, Extranjeros, Asignaciones — ver ClosedXmlImportacionParser).
///
/// Este test no es solo un camino feliz: reproduce, con datos reales, un
/// hallazgo confirmado leyendo EjecutarImportacionCommandHandler. Como
/// Centros_Plataformas ya no puede crear Cliente/Centro nuevos (mismo motivo
/// que ImportarClientesTests — Fase 10 exige CIF/Empresa que este formato no
/// recoge), cualquier Asignación de la hoja Asignaciones que dependa de un
/// Centro nuevo de ese mismo archivo pierde su Centro: la primera búsqueda
/// de EjecutarImportacionCommandHandler indexa <c>centrosPorNombre</c> UNA
/// vez contra la base de datos, antes de procesar nada, y el bucle de
/// Asignaciones que no encuentra el Centro hace <c>continue</c> sin más — a
/// diferencia del resto del handler, esta rama nunca añade nada a
/// <see cref="CaeManager.Application.Importacion.ItemImportacionDto"/>
/// Omitidos. El resultado: el paso 2 promete "Asignaciones nuevas: 1", la
/// pantalla final confirma "Asignaciones creadas: 0" y en ningún sitio se
/// explica por qué — a diferencia de la fila de Cliente/Centro, que sí
/// aparece en Omitidos con motivo explícito. Es justo la clase de pérdida
/// silenciosa de datos que esta ronda de tests E2E se propuso encontrar
/// (ver el hallazgo en el informe de la tarea) — Asignaciones es la
/// relación Trabajador↔Centro que decide qué acceso CAE tiene cada
/// trabajador, así que perderla en silencio no es un detalle menor.
/// </summary>
[Collection("AppCollection")]
public class ImportacionTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Importacion_CAE_crea_Empresa_Trabajador_y_Documento_pero_pierde_la_Asignacion_en_silencio()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var nombreCentro = $"CAE Centro {sufijo}";
        var nombreTrabajador = "CAE";
        var apellidosTrabajador = $"Import {sufijo}";
        var dniTrabajador = Ayudas.GenerarDniValido(88_400_001);
        var fechaEmision = DateTime.UtcNow.Date.AddDays(-30);

        var libro = new XLWorkbook();

        var hojaCentros = libro.Worksheets.Add("Centros_Plataformas");
        hojaCentros.Cell(5, 1).Value = "N";
        hojaCentros.Cell(5, 2).Value = nombreCentro;

        var hojaEmpleados = libro.Worksheets.Add("Empleados");
        hojaEmpleados.Cell(4, 1).Value = 1;
        hojaEmpleados.Cell(4, 2).Value = nombreTrabajador;
        hojaEmpleados.Cell(4, 3).Value = apellidosTrabajador;
        hojaEmpleados.Cell(4, 4).Value = dniTrabajador;
        hojaEmpleados.Cell(4, 7).Value = fechaEmision; // Columna G: Apto médico laboral (ver ColumnasDocumentos).

        // Hoja obligatoria para el parser (ver ClosedXmlImportacionParser) —
        // sin datos, el bucle de Empleados/Extranjeros la deja vacía sin
        // generar ninguna Empresa ni Trabajador de esta hoja.
        libro.Worksheets.Add("Extranjeros (Ibertec GmbH)");

        var hojaAsignaciones = libro.Worksheets.Add("Asignaciones");
        hojaAsignaciones.Cell(4, 4).Value = "Centro 1"; // Cabecera de la única columna de centro…
        hojaAsignaciones.Cell(4, 5).Value = "TOTAL CENTROS"; // …y el marcador que cierra el barrido de columnas.
        hojaAsignaciones.Cell(5, 1).Value = 1;
        hojaAsignaciones.Cell(5, 2).Value = nombreTrabajador;
        hojaAsignaciones.Cell(5, 3).Value = apellidosTrabajador;
        hojaAsignaciones.Cell(5, 4).Value = "X"; // Marca la asignación de esta fila a la única columna de centro.

        var rutaExcel = Ayudas.GuardarLibroDePruebaEnDisco(libro, "cuadro-control-cae.xlsx");

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        try
        {
            await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
            await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/importacion");

            await page.Locator("input[type=\"file\"]").SetInputFilesAsync(rutaExcel);

            // --- Paso 2: el plan promete las 6 altas, incluida la Asignación ---
            await page.GetByText("2. Revisa el plan de importación").WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Clientes nuevos"));
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Centros nuevos"));
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Empresas nuevas"));
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Trabajadores nuevos"));
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Documentos nuevos"));
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Asignaciones nuevas"));

            await page.GetByText("Confirmar importación").ClickAsync();

            // --- Resultado: Empresa/Trabajador/Documento sí se crean; Cliente/Centro se omiten con motivo;
            // Asignaciones queda en 0 SIN ningún Omitido que lo explique — la pérdida silenciosa. ---
            // GetByText a secas ambigua con el toast "Importación completada." (con punto).
            await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Importación completada" })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            Assert.Equal("0", await Ayudas.LeerMetricaAsync(page, "Clientes creados"));
            Assert.Equal("0", await Ayudas.LeerMetricaAsync(page, "Centros creados"));
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Empresas creadas"));
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Trabajadores creados"));
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Documentos creados"));
            Assert.Equal("0", await Ayudas.LeerMetricaAsync(page, "Asignaciones creadas"));

            var filasOmitidas = page.Locator(".tabla-datos tbody tr");
            await filasOmitidas.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
            // Un único Omitido — el de Cliente/Centro. La Asignación perdida no deja rastro aquí.
            await Expect(filasOmitidas).ToHaveCountAsync(1);
            await Expect(filasOmitidas.First).ToContainTextAsync(nombreCentro);
            await Expect(filasOmitidas.First).ToContainTextAsync("CIF");
            await Expect(page.Locator(".tabla-datos")).Not.ToContainTextAsync("Asignaciones");
        }
        finally
        {
            File.Delete(rutaExcel);
        }

        // --- Verificación real de las tres altas que sí ocurrieron ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/empresas");
        await page.GetByPlaceholder("Buscar por razón social…").FillAsync("Ibertec S.A.");
        await page.Locator(".tarjeta-fila-acordeon", new PageLocatorOptions { HasText = "Ibertec S.A." })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos");
        await page.GetByPlaceholder("Buscar por propietario o tipo de documento…").FillAsync(apellidosTrabajador);
        var filaDocumento = page.Locator("tr", new PageLocatorOptions { HasText = apellidosTrabajador });
        await filaDocumento.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await Expect(filaDocumento).ToContainTextAsync("Apto médico laboral");

        // --- Verificación real de lo que NO ocurrió: ni Cliente ni Centro existen con este nombre ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");
        await page.GetByPlaceholder("Buscar por nombre…").FillAsync(nombreCentro);
        await page.WaitForTimeoutAsync(500);
        await Expect(page.Locator("tr", new PageLocatorOptions { HasText = nombreCentro })).ToHaveCountAsync(0);

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/centros");
        await page.GetByPlaceholder("Buscar centro, cliente o empresa…").FillAsync(nombreCentro);
        await page.WaitForTimeoutAsync(500);
        await Expect(page.GetByText(nombreCentro)).Not.ToBeVisibleAsync();
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
