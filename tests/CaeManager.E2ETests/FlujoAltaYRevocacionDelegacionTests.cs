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
        // Exact: true -- sin esto, GetByText hace match por substring y
        // también resuelve el párrafo "Se creará una organización..." (que
        // contiene "creará", que empieza igual que "Crear").
        await modalNueva.GetByText("Crear", new LocatorGetByTextOptions { Exact = true }).ClickAsync();
        await modalNueva.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        var tarjeta = page.Locator(".tarjeta-delegacion", new PageLocatorOptions { HasText = nombreClienteDelegante });
        await tarjeta.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await Expect(tarjeta).ToContainTextAsync("Activa");
        await Expect(tarjeta.GetByText("Revocar acceso")).ToBeVisibleAsync();

        // --- Operar el Delegated Workspace recién creado ---
        // SelectorClienteActivo (en el layout) solo carga _clientes en
        // OnInitializedAsync — no hay ningún mecanismo que lo refresque en
        // vivo tras crear una delegación en la misma navegación in-app, así
        // que sin recargar la página la nueva delegación no está todavía en
        // el <select> (ver SelectorClienteActivo.razor.cs). Un reload es
        // además lo que haría un usuario real antes de esperar verla ahí.
        //
        // Esperar NetworkIdle tras el reload (intento anterior) no basta:
        // deja el circuito de SignalR "asentándose" de fondo y esa actividad
        // de reconexión se solapa con el WaitForLoadStateAsync(NetworkIdle)
        // interno de CambiarClienteActivoAsync justo después, adelantando su
        // resolución antes de que la navegación real (el <form> POST que
        // dispara microinteracciones.js) termine — el GotoAsync("/clientes")
        // posterior llegaba entonces con esa navegación todavía en vuelo y el
        // navegador la abortaba (net::ERR_ABORTED). Esperar a que la propia
        // opción de la delegación exista en el <select> es una señal
        // concreta de que OnInitializedAsync ya corrió con el circuito
        // interactivo de verdad, no solo de que la red esté momentáneamente
        // en silencio.
        await page.ReloadAsync();
        // State: Attached, no el Visible por defecto -- un <option> nunca se
        // reporta "visible" para Playwright mientras el <select> está
        // cerrado (no tiene caja de layout real), así que WaitForAsync con
        // el estado por defecto nunca resolvía aunque el elemento ya
        // existiera en el DOM (confirmado en CI: 34 reintentos, siempre
        // resuelto pero "hidden").
        await page.Locator(".selector-cliente-activo option", new PageLocatorOptions { HasText = nombreClienteDelegante })
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 15_000 });

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
