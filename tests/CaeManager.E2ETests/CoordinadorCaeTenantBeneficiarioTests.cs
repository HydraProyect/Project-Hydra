using System.Collections.Concurrent;
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

    /// <summary>
    /// Causa del rojo intermitente de la prueba de abajo en CI (dos veces: «Timeout 5000ms
    /// exceeded» esperando <c>.selector-tenant-panel</c>): la landing del Coordinador CAE
    /// (Mi trabajo) mataba su circuito de Blazor ~1 s después de cargar. Su cola traía dos filas
    /// hermanas con el mismo Id (el mismo Trabajador, asignado a dos Centros, sin el mismo Tipo
    /// de documento) y Blazor lanza «Attempting to return wrong pooled instance» al repintar con
    /// claves repetidas. Con el circuito muerto la pantalla queda inerte y el aviso de error
    /// aparece. La otra prueba solo lo notaba si su primer clic caía después de esa muerte,
    /// según la velocidad del runner; esta deja pasar la ventana a propósito y es determinista.
    /// </summary>
    [Fact]
    public async Task La_landing_del_Coordinador_CAE_mantiene_el_circuito_vivo_y_el_selector_abre_con_la_pantalla_ya_asentada()
    {
        var email = await fixture.LeerValorSqlAsync(
            """SELECT "Email" FROM "AspNetUsers" WHERE "Email" LIKE 'coordinador1.%@caemanager.local' """);

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, email, Ayudas.ContrasenaUsuariosPrueba);

        // Barrera: la cola ya se pintó (las dos consultas de la página terminaron).
        await page.Locator(".mi-trabajo-chips").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        // El circuito moría en el primer segundo tras pintar la cola; se deja pasar con margen.
        await page.WaitForTimeoutAsync(4_000);

        // Una observación positiva que exige circuito vivo: el disparador del selector es un islote interactivo.
        await Ayudas.AbrirSelectorTenantAsync(page);
        await Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
    }

    [Fact]
    public async Task El_Tenant_beneficiario_elegido_sobrevive_al_menu_lateral_del_Coordinador_CAE()
    {
        var email = await fixture.LeerValorSqlAsync(
            """SELECT "Email" FROM "AspNetUsers" WHERE "Email" LIKE 'coordinador1.%@caemanager.local' """);
        var tenantPizza = await fixture.LeerValorSqlAsync(
            """SELECT "Id"::text FROM "Tenants" WHERE "Nombre" = @n""", ("n", PizzaPlanet));

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        // Diagnóstico: toda respuesta que toque la cookie de selección, con su hora, para saber quién la retira.
        var setCookies = new ConcurrentQueue<string>();
        page.Response += async (_, r) =>
        {
            try
            {
                var h = await r.AllHeadersAsync();
                if (h.TryGetValue("set-cookie", out var v) && v.Contains("cae_cliente_activo"))
                    setCookies.Enqueue(
                        $"{DateTime.UtcNow:HH:mm:ss.fff} {r.Request.Method} {r.Url} {r.Status} :: {v[..Math.Min(v.Length, 100)].Replace('\n', ' ')}");
            }
            catch (PlaywrightException)
            {
            }
        };

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, email, Ayudas.ContrasenaUsuariosPrueba);

        var donde = "inicio";
        try
        {
            await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, PizzaPlanet);
            donde = "tras elegir Pizza Planet";
            await RecorridoFichas360.ComprobarQueElTenantSigueActivoAsync(page, tenantPizza, donde);

            foreach (var destino in new[] { "trabajadores", "centros", "empresas", "trabajadores", "centros", "empresas" })
            {
                await page.Locator($"nav a.nav-item[href='{destino}']").First.ClickAsync();
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                donde = $"menú lateral → {destino}";
                await RecorridoFichas360.ComprobarQueElTenantSigueActivoAsync(page, tenantPizza, donde);
                await page.WaitForTimeoutAsync(5_000);
                donde = $"{destino} tras revalidar el circuito";
                await RecorridoFichas360.ComprobarQueElTenantSigueActivoAsync(page, tenantPizza, donde);
            }

            await page.ReloadAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            donde = "recarga final";
            await RecorridoFichas360.ComprobarQueElTenantSigueActivoAsync(page, tenantPizza, donde);
        }
        catch (PlaywrightException ex)
        {
            var cookies = string.Join(", ", (await contexto.CookiesAsync()).Select(c => c.Name));
            throw new Xunit.Sdk.XunitException(
                $"Fallo en «{donde}» ({page.Url}). Cookies del navegador: [{cookies}]. Set-Cookie de cae_cliente_activo vistos:\n"
                + string.Join("\n", setCookies) + "\n" + ex.Message[..Math.Min(ex.Message.Length, 300)]);
        }
    }
}
