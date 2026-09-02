using ClosedXML.Excel;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Cubre /importacion (Importacion.razor, el importador del "Cuadro de
/// Control CAE") — sin ningún E2E hasta ahora, el más complejo de las 5
/// pantallas de este bloque (4 hojas heterogéneas: Centros_Plataformas,
/// Empleados, Extranjeros, Asignaciones — ver ClosedXmlImportacionParser).
///
/// Este test guarda la invariante «nada se descarta en silencio»
/// (IMPORTACION.md § 3 bis, ratificada por DCR-12 decisión B, propietario
/// 2026-08-24) sobre el caso que más cuesta cumplirla. Como
/// Centros_Plataformas ya no puede crear Cliente/Centro nuevos (mismo motivo
/// que ImportarClientesTests — Fase 10 exige CIF/Empresa que este formato no
/// recoge), cualquier Asignación de la hoja Asignaciones que dependa de un
/// Centro nuevo de ese mismo archivo se queda sin su Centro: la búsqueda de
/// EjecutarImportacionCommandHandler indexa <c>centrosPorNombre</c> UNA vez
/// contra la base de datos, antes de procesar nada, así que el Centro que el
/// archivo declaraba pero no pudo crearse nunca aparece ahí.
///
/// Hasta el 2026-09-02 este test exigía exactamente lo contrario: se llamaba
/// "…pierde_la_Asignacion_en_silencio" y afirmaba, como resultado correcto,
/// que esa pérdida no dejaba rastro en
/// <see cref="CaeManager.Application.Importacion.ItemImportacionDto"/>
/// Omitidos. Se había escrito deliberadamente para congelar un defecto ya
/// conocido, y DCR-12 le retiró la autoridad: el contrato manda sobre el
/// test. Ahora exige lo que el contrato promete — la Asignación puede
/// omitirse, pero aparece en Omitidos nombrando el Centro que faltó y
/// distinguiendo que venía en este mismo archivo y no pudo crearse.
/// Asignaciones es la relación Trabajador↔Centro que decide qué acceso CAE
/// tiene cada trabajador: perderla sin traza no es un detalle menor.
/// </summary>
[Collection("AppCollection")]
public class ImportacionTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Importacion_CAE_crea_Empresa_Trabajador_y_Documento_y_registra_la_Asignacion_omitida_con_su_motivo()
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
        hojaEmpleados.Cell(4, 7).Value = fechaEmision; // Columna G: Certificado de aptitud médica (ver ColumnasDocumentos).

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

            // Wizard de 5 pasos: "cae" ya viene preseleccionada en el paso 1
            // (elegir plantilla), pero el input de archivo solo existe en el
            // paso 2 — hay que confirmar la plantilla primero.
            await page.GetByText("Continuar con Importación CAE completa").ClickAsync();
            await page.Locator("input[type=\"file\"]").SetInputFilesAsync(rutaExcel);

            var botonVerPlan = page.GetByText("Ver plan de importación");
            await Expect(botonVerPlan).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 15_000 });
            await botonVerPlan.ClickAsync();

            // --- Paso 3 "Revisar plan": el plan promete las 6 altas, incluida la
            // Asignación (Importacion.razor.cs, NombresPasos — el wizard
            // unificado de 5 pasos que sustituyó a los 4 por plantilla, tarea
            // #41, ya no usa el título "N. Revisa el plan de importación" de
            // aquellos ni tarjetas TarjetaMetrica por entidad: aquí el plan es
            // una única tabla ProyectarFilas con una fila "Crear X" por alta). ---
            await page.GetByText("Revisar plan").WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            var tablaPlan = page.Locator(".tabla-plan-importacion-envoltorio .tabla-datos");
            await Expect(page.GetByText("6 se crearán")).ToBeVisibleAsync();
            await Expect(tablaPlan).ToContainTextAsync("Crear cliente");
            await Expect(tablaPlan).ToContainTextAsync("Crear centro");
            await Expect(tablaPlan).ToContainTextAsync("Crear empresa");
            await Expect(tablaPlan).ToContainTextAsync($"{nombreTrabajador} {apellidosTrabajador}");
            await Expect(tablaPlan).ToContainTextAsync("Crear documento");
            await Expect(tablaPlan).ToContainTextAsync("Crear asignación");

            await page.GetByText("Continuar a confirmar").ClickAsync();
            await page.GetByText("He revisado el plan y quiero escribir estos datos").ClickAsync();
            await page.GetByText("Importar ahora").ClickAsync();

            // --- Resultado: Empresa/Trabajador/Documento sí se crean, y las dos filas que
            // no pudieron importarse aparecen AMBAS en Omitidos con su motivo — la de
            // Cliente/Centro (le falta el CIF, que esta plantilla no recoge) y la
            // Asignación que se quedó sin su Centro. Antes de DCR-12 B esta última
            // desaparecía sin dejar rastro, y este test lo exigía así.
            // El reporte del wizard unificado agrega "Creados" en un único
            // número (Importacion.razor, paso 5) en vez de una tarjeta por
            // entidad — 3 (empresa + trabajador + documento).
            // ".titulo-reporte-importacion" en vez de un rol Heading:
            // es un <span>, no un <h#> — y a secas es ambiguo con el toast
            // "Importación completada." (con punto). ---
            await page.Locator(".titulo-reporte-importacion")
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            Assert.Equal("3", await Ayudas.LeerMetricaAsync(page, "Creados"));
            Assert.Equal("0", await Ayudas.LeerMetricaAsync(page, "Avisos"));
            Assert.Equal("2", await Ayudas.LeerMetricaAsync(page, "Omitidos"));

            var filasOmitidas = page.Locator(".tabla-datos tbody tr");
            await filasOmitidas.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
            await Expect(filasOmitidas).ToHaveCountAsync(2);

            // La fila de Cliente/Centro, que ya se registraba antes de DCR-12 B.
            var filaClienteCentro = page.Locator(".tabla-datos tbody tr", new PageLocatorOptions { HasText = "CIF" });
            await Expect(filaClienteCentro).ToContainTextAsync(nombreCentro);

            // La Asignación: el contrato exige que quede registrada nombrando el
            // Centro que faltó, y que distinga que venía en este mismo archivo y no
            // pudo crearse (frente al Centro que el archivo ni siquiera declara).
            var filaAsignacion = page.Locator(".tabla-datos tbody tr", new PageLocatorOptions { HasText = "Asignaciones" });
            await Expect(filaAsignacion).ToHaveCountAsync(1);
            await Expect(filaAsignacion).ToContainTextAsync(nombreCentro);
            await Expect(filaAsignacion).ToContainTextAsync(dniTrabajador);
            await Expect(filaAsignacion).ToContainTextAsync("Centros_Plataformas");
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
        await Expect(filaDocumento).ToContainTextAsync("Certificado de aptitud médica");

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
