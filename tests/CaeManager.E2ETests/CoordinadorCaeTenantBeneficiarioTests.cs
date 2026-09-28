using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// P0 del piloto Outbound (2026-09-28), en staging con la cuenta demo del Coordinador CAE:
/// tras elegir Pizza Planet en el selector y abrir «Trabajadores» desde el menú lateral
/// (navegación mejorada), el selector volvía al Tenant de origen. Aquí, el Coordinador CAE de
/// la siembra de escenarios (Operador CAE externo; alcanza el Tenant beneficiario por
/// delegación heredada) recorre el menú lateral con el Tenant elegido.
/// </summary>
[Collection("AppCollectionEscenariosDireccion")]
public class CoordinadorCaeTenantBeneficiarioTests(WebAppFixtureEscenariosDireccion fixture)
{
    private const string PizzaPlanet = "Pizza Planet S.L.";

    [Fact]
    public async Task El_Tenant_beneficiario_elegido_sobrevive_al_menu_lateral_del_Coordinador_CAE()
    {
        var email = await fixture.LeerValorSqlAsync(
            """SELECT "Email" FROM "AspNetUsers" WHERE "Email" LIKE 'coordinador1.%@caemanager.local' """);
        var tenantPizza = await fixture.LeerValorSqlAsync(
            """SELECT "Id"::text FROM "Tenants" WHERE "Nombre" = @n""", ("n", PizzaPlanet));

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, email, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);

        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, PizzaPlanet);
        await RecorridoFichas360.ComprobarQueElTenantSigueActivoAsync(page, tenantPizza, "tras elegir Pizza Planet");

        foreach (var destino in new[] { "trabajadores", "centros", "empresas", "trabajadores", "centros", "empresas" })
        {
            await page.Locator($"nav a.nav-item[href='{destino}']").First.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await RecorridoFichas360.ComprobarQueElTenantSigueActivoAsync(page, tenantPizza, $"menú lateral → {destino}");
            await page.WaitForTimeoutAsync(5_000);
            await RecorridoFichas360.ComprobarQueElTenantSigueActivoAsync(page, tenantPizza, $"{destino} tras revalidar el circuito");
        }

        await page.ReloadAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await RecorridoFichas360.ComprobarQueElTenantSigueActivoAsync(page, tenantPizza, "recarga final");
    }
}
