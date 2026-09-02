using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Cubre la plantilla Combinada del asistente unificado (/importacion,
/// Importacion.razor — Plantillas["combinada"]), alcanzada desde
/// /clientes/importar-combinado, que desde H-1 (2026-09-02) redirige ahí en
/// vez de renderizar su propia página (ImportarCombinado.razor pasó a ser un
/// stub de redirección — el hub absorbe las migraciones tabulares, 0 enlaces
/// entrantes propios). A diferencia de la plantilla simple de Clientes (ver
/// ImportarClientesTests), esta sí recoge CIF y Empresa, así que es la única
/// de las plantillas de un solo Excel que puede dar de alta Cliente y Centro
/// nuevos de verdad (ver ClosedXmlPlantillaCombinadaService y
/// EjecutarImportacionCombinadaCommandHandler) — un único libro de 4 hojas
/// (Clientes, Empresas, Centros, Trabajadores) encadena las cuatro altas.
///
/// La misma fila que prueba el alta también lleva, en la propia hoja
/// Clientes, una segunda fila con un CIF deliberadamente inválido (dígito de
/// control corrupto vía Ayudas.InvalidarCif) — es el caso "malformed CIF"
/// que este bloque de tests debe demostrar que la app detecta y omite en
/// vez de importar en silencio: la fila con CIF roto nunca llega a
/// PlanImportacionCombinadaDto.Clientes, así que ya aparece en Omitidos
/// desde el paso 2 (a diferencia de ImportarClientesTests, donde la omisión
/// solo se descubre al confirmar) y se confirma que jamás se creó el Cliente.
/// </summary>
[Collection("AppCollection")]
public class ImportarCombinadoTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Importacion_combinada_crea_Cliente_Empresa_Centro_y_Trabajador_y_omite_la_fila_con_CIF_invalido()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var razonSocialCliente = $"Combinado Cliente {sufijo}";
        var cifCliente = Ayudas.GenerarCifValido(9_996_001);
        var razonSocialClienteInvalido = $"Combinado Cliente Invalido {sufijo}";
        var cifInvalido = Ayudas.InvalidarCif(Ayudas.GenerarCifValido(9_996_002));
        var razonSocialEmpresa = $"Combinado Empresa {sufijo}";
        var nombreCentro = $"Combinado Centro {sufijo}";
        var apellidosTrabajador = $"Combinado {sufijo}";
        var dniTrabajador = Ayudas.GenerarDniValido(88_100_001);

        var libro = new XLWorkbook();

        var hojaClientes = libro.Worksheets.Add("Clientes");
        hojaClientes.Cell(1, 1).Value = "Razón social";
        hojaClientes.Cell(1, 2).Value = "CIF";
        hojaClientes.Cell(1, 3).Value = "Crítico (C/N)";
        hojaClientes.Cell(2, 1).Value = razonSocialCliente;
        hojaClientes.Cell(2, 2).Value = cifCliente;
        hojaClientes.Cell(2, 3).Value = "N";
        // Fila deliberadamente inválida — CIF con dígito de control roto.
        hojaClientes.Cell(3, 1).Value = razonSocialClienteInvalido;
        hojaClientes.Cell(3, 2).Value = cifInvalido;
        hojaClientes.Cell(3, 3).Value = "N";

        var hojaEmpresas = libro.Worksheets.Add("Empresas");
        hojaEmpresas.Cell(1, 1).Value = "Razón social";
        hojaEmpresas.Cell(1, 2).Value = "Clientes asociados (separados por ;)";
        hojaEmpresas.Cell(2, 1).Value = razonSocialEmpresa;
        hojaEmpresas.Cell(2, 2).Value = razonSocialCliente;

        var hojaCentros = libro.Worksheets.Add("Centros");
        hojaCentros.Cell(1, 1).Value = "Nombre";
        hojaCentros.Cell(1, 2).Value = "Cliente";
        hojaCentros.Cell(1, 3).Value = "Empresa";
        hojaCentros.Cell(2, 1).Value = nombreCentro;
        hojaCentros.Cell(2, 2).Value = razonSocialCliente;
        hojaCentros.Cell(2, 3).Value = razonSocialEmpresa;

        var hojaTrabajadores = libro.Worksheets.Add("Trabajadores");
        hojaTrabajadores.Cell(1, 1).Value = "Nombre";
        hojaTrabajadores.Cell(1, 2).Value = "Apellidos";
        hojaTrabajadores.Cell(1, 3).Value = "DNI";
        hojaTrabajadores.Cell(1, 4).Value = "Empresa";
        hojaTrabajadores.Cell(2, 1).Value = "Combinado";
        hojaTrabajadores.Cell(2, 2).Value = apellidosTrabajador;
        hojaTrabajadores.Cell(2, 3).Value = dniTrabajador;
        hojaTrabajadores.Cell(2, 4).Value = razonSocialEmpresa;

        var rutaExcel = Ayudas.GuardarLibroDePruebaEnDisco(libro, "importar-combinado.xlsx");

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        try
        {
            await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
            // /clientes/importar-combinado redirige aquí con la plantilla ya preseleccionada (H-1).
            await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes/importar-combinado");
            await Expect(page).ToHaveURLAsync(new Regex(@"/importacion\?plantilla=combinada$"));

            await page.GetByText("Continuar con Combinada: Cliente + Empresas + Centros + Trabajadores").ClickAsync();
            await page.Locator("input[type=\"file\"]").SetInputFilesAsync(rutaExcel);

            // --- Paso 2 "Revisar plan": las 4 altas nuevas y la fila inválida ya aislada ---
            var botonVerPlan = page.GetByText("Ver plan de importación");
            await Expect(botonVerPlan).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 15_000 });
            await botonVerPlan.ClickAsync();

            // GetByText("Revisar plan") a secas es ambigua: el nombre del paso
            // 3 aparece tanto en el botón del stepper del wizard como en el <h2>
            // de esta sección — el rol Heading es inequívoco.
            await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Revisar plan" })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            await Expect(page.GetByText("4 se crearán")).ToBeVisibleAsync();
            await Expect(page.GetByText("1 se omitirán")).ToBeVisibleAsync();

            var filaOmitidaEnPlan = page.Locator(".tabla-plan-importacion-envoltorio .tabla-datos tbody tr", new PageLocatorOptions { HasText = razonSocialClienteInvalido });
            await filaOmitidaEnPlan.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
            await Expect(filaOmitidaEnPlan).ToContainTextAsync("no es válido");

            await page.GetByText("Continuar a confirmar").ClickAsync();
            await page.GetByText("He revisado el plan y quiero escribir estos datos").ClickAsync();
            await page.GetByText("Importar ahora").ClickAsync();

            // --- Resultado: las 4 altas reales, la fila inválida sigue fuera ---
            await page.Locator(".titulo-reporte-importacion")
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            Assert.Equal("4", await Ayudas.LeerMetricaAsync(page, "Creados"));
            Assert.Equal("0", await Ayudas.LeerMetricaAsync(page, "Actualizados"));
            Assert.Equal("0", await Ayudas.LeerMetricaAsync(page, "Avisos"));
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Omitidos"));

            // --- Verificación real: las 4 entidades existen, la inválida no ---
            // F3b (2026-08-26): el Cliente importado (y el CIF inválido que
            // debía omitirse) ya no se verifican en /clientes —
            // ObtenerClientesQuery sigue congelada por D2 hasta F4 y, con los
            // escritores redirigidos a Empresa, esa pantalla queda vacía en
            // cualquier entorno (decisión explícita: "aceptar el vacío", ver
            // f3b-decision-d2-transicion-acotada-2026-08-25.md). El Cliente
            // importado sí existe como fila en /empresas (EsCritico != null);
            // la fila con CIF inválido nunca se creó en ninguna tabla, así
            // que su ausencia ahí sigue siendo una comprobación real, no
            // degenerada.
            await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/empresas");
            await page.GetByPlaceholder("Buscar por razón social…").FillAsync(razonSocialCliente);
            await page.Locator(".tarjeta-fila-acordeon", new PageLocatorOptions { HasText = razonSocialCliente })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

            await page.GetByPlaceholder("Buscar por razón social…").FillAsync(razonSocialClienteInvalido);
            await page.WaitForTimeoutAsync(500);
            await Expect(page.Locator(".tarjeta-fila-acordeon", new PageLocatorOptions { HasText = razonSocialClienteInvalido })).ToHaveCountAsync(0);

            await page.GetByPlaceholder("Buscar por razón social…").FillAsync(razonSocialEmpresa);
            await page.Locator(".tarjeta-fila-acordeon", new PageLocatorOptions { HasText = razonSocialEmpresa })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

            await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/centros");
            await page.GetByPlaceholder("Buscar centro, cliente o empresa…").FillAsync(nombreCentro);
            await page.GetByText(nombreCentro).WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

            await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
            await page.GetByPlaceholder("Buscar por nombre, apellidos, alias o DNI…").FillAsync(apellidosTrabajador);
            await page.Locator("tr", new PageLocatorOptions { HasText = apellidosTrabajador })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        }
        finally
        {
            File.Delete(rutaExcel);
        }
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
    private static IPageAssertions Expect(IPage page) => Assertions.Expect(page);
}
