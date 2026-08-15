using ClosedXML.Excel;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Cubre /clientes/importar-combinado (ImportarCombinado.razor) — sin
/// ningún E2E hasta ahora. A diferencia de la plantilla simple de Clientes
/// (ver ImportarClientesTests), esta sí recoge CIF y Empresa, así que es la
/// única de las plantillas de un solo Excel que puede dar de alta Cliente y
/// Centro nuevos de verdad (ver ClosedXmlPlantillaCombinadaService y
/// EjecutarImportacionCombinadaCommandHandler) — un único libro de 4 hojas
/// (Clientes, Empresas, Centros, Trabajadores) encadena las cuatro altas.
///
/// La misma fila que prueba el alta también lleva, en la propia hoja
/// Clientes, una segunda fila con un CIF deliberadamente inválido (dígito de
/// control corrupto vía Ayudas.InvalidarCif) — es el caso "malformed CIF"
/// que este bloque de tests debe demostrar que la app detecta y omite en
/// vez de importar en silencio: la fila con CIF roto nunca llega a
/// PlanImportacionCombinadaDto.Clientes, así que ni siquiera cuenta como
/// "nueva" en el paso 2 — aparece solo en Omitidos, con el motivo exacto, y
/// se confirma que jamás se creó el Cliente.
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
            await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes/importar-combinado");

            await page.Locator("input[type=\"file\"]").SetInputFilesAsync(rutaExcel);

            // --- Paso 2: el plan cuenta las 4 altas nuevas y aísla la fila inválida ---
            await page.GetByText("2. Revisa el plan de importación").WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Clientes nuevos"));
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Empresas nuevas"));
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Centros nuevos"));
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Trabajadores nuevos"));

            var filaOmitida = page.Locator(".tabla-datos tbody tr", new PageLocatorOptions { HasText = razonSocialClienteInvalido });
            await filaOmitida.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
            await Expect(filaOmitida).ToContainTextAsync("no es válido");

            await page.GetByText("Confirmar importación").ClickAsync();

            // --- Resultado: las 4 altas reales, la fila inválida sigue fuera ---
            // GetByText a secas ambigua con el toast "Importación completada." (con punto).
            await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Importación completada" })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Clientes creados"));
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Empresas creadas"));
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Centros creados"));
            Assert.Equal("1", await Ayudas.LeerMetricaAsync(page, "Trabajadores creados"));

            // --- Verificación real: las 4 entidades existen, la inválida no ---
            await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");
            await page.GetByPlaceholder("Buscar por nombre…").FillAsync(razonSocialCliente);
            await page.Locator("tr", new PageLocatorOptions { HasText = razonSocialCliente })
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

            await page.GetByPlaceholder("Buscar por nombre…").FillAsync(razonSocialClienteInvalido);
            await page.WaitForTimeoutAsync(500);
            await Expect(page.Locator("tr", new PageLocatorOptions { HasText = razonSocialClienteInvalido })).ToHaveCountAsync(0);

            await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/empresas");
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
}
