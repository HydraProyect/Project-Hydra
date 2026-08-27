using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// F4-P0 (2026-08-27): <c>ObtenerClientesQuery</c> leía la tabla legacy
/// <c>Clientes</c>, congelada sin escrituras desde F3b-Cliente (PR #279) —
/// cualquier Cliente dado de alta a través del formulario real de
/// <c>/clientes</c> escribía en <c>Empresas</c> (<c>CrearClienteCommand</c>)
/// pero nunca aparecía en su propio listado. Reproduce el ciclo completo por
/// UI real (no por seeder) para demostrar el contrato correcto extremo a
/// extremo, no solo a nivel de query.
/// </summary>
[Collection("AppCollection")]
public class ClientesListaLeeEmpresasE2ETests(WebAppFixture fixture)
{
    [Fact]
    public async Task Un_Cliente_creado_desde_el_formulario_real_aparece_de_inmediato_en_su_propio_listado()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var razonSocial = $"P0 Clientes {sufijo}";

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");

        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "+ Nuevo cliente" }).First.ClickAsync();
        await page.GetByLabel("Razón social").FillAsync(razonSocial);
        await page.GetByLabel("CIF", new PageGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_997_801));
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Guardar", Exact = true }).ClickAsync();

        // El drawer se cierra tras guardar — señal de que el comando ya
        // persistió, no de que la lista se haya actualizado.
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Nuevo cliente" })).Not.ToBeVisibleAsync();

        // La prueba real del P0: el Cliente recién creado aparece en su
        // propio listado sin recargar manualmente ni buscar por CIF — antes
        // de esta corrección, esta fila jamás aparecía (ObtenerClientesQuery
        // seguía leyendo la tabla Clientes, sin escrituras desde F3b).
        await Expect(page.GetByText(razonSocial)).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
