using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Cubre el alta de un Cliente Delegante nuevo (crea el tenant, la
/// delegación activa y asigna al creador como Operador Delegado en el mismo
/// paso), operar dentro de su Delegated Workspace, y la revocación
/// (Horizonte 1.6 de MACRO_PLAN_2026-08-13.md) — complementa a
/// FlujoDelegatedWorkspaceTests (que solo cambia entre workspaces ya
/// existentes) y FlujoSoporteTests (mecanismo de acceso distinto: motivo +
/// ventana, no alta/revocación comercial).
/// </summary>
[Collection("AppCollection")]
public class FlujoAltaYRevocacionDelegacionTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Alta_de_delegacion_operar_el_workspace_y_revocacion()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var nombreClienteDelegante = $"Delegación E2E {sufijo}";

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/delegaciones");

        // --- Alta: crea el tenant + delegación activa + operador en un solo paso ---
        await page.GetByText("Nueva delegación").ClickAsync();
        var modalNueva = page.GetByRole(AriaRole.Dialog).Filter(new LocatorFilterOptions { HasText = "Nueva delegación" });
        await modalNueva.GetByLabel("Nombre del Cliente Delegante").FillAsync(nombreClienteDelegante);
        await modalNueva.GetByText("Crear").ClickAsync();
        await modalNueva.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        var tarjeta = page.Locator(".tarjeta-delegacion", new PageLocatorOptions { HasText = nombreClienteDelegante });
        await tarjeta.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await Expect(tarjeta).ToContainTextAsync("Activa");
        await Expect(tarjeta.GetByText("Revocar acceso")).ToBeVisibleAsync();

        // --- Operar el Delegated Workspace recién creado ---
        // El tenant nuevo no tiene datos propios todavía (se acaba de crear),
        // así que la comprobación real es que el selector de Cliente activo
        // cambia y la app deja de estar en la Consultora de origen.
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, nombreClienteDelegante);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");
        await Expect(page.Locator(".selector-cliente-activo")).ToHaveValueAsync(
            await page.Locator(".selector-cliente-activo option", new PageLocatorOptions { HasText = nombreClienteDelegante }).GetAttributeAsync("value") ?? string.Empty);

        // Vuelve al origen antes de revocar — revocar el workspace en el que
        // se está operando en ese momento no es el escenario que se quiere
        // probar aquí (además, DesactivarDelegacionTenantCommand actúa sobre
        // la delegación, no exige haber cambiado de vuelta, pero hacerlo así
        // es el camino real que seguiría un usuario).
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, Ayudas.NombreTenantOrigenPorDefecto);

        // --- Revocación ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/delegaciones");
        var tarjetaTrasVolver = page.Locator(".tarjeta-delegacion", new PageLocatorOptions { HasText = nombreClienteDelegante });
        await tarjetaTrasVolver.GetByText("Revocar acceso").ClickAsync();

        var modalRevocar = page.GetByRole(AriaRole.Dialog).Filter(new LocatorFilterOptions { HasText = "Revocar el acceso" });
        await modalRevocar.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await modalRevocar.GetByText("Revocar acceso").ClickAsync();
        await modalRevocar.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        await Expect(tarjetaTrasVolver).ToContainTextAsync("Revocada");
        await Expect(tarjetaTrasVolver.GetByText("Reactivar")).ToBeVisibleAsync();
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
