using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Recorrido compartido del P0 del piloto Outbound (2026-09-28): con un Tenant
/// beneficiario activo que no es el de origen, abrir la lista de Trabajadores y desde
/// ella Trabajador 360 devolvía el contexto al Tenant de origen con el aviso de acceso
/// no vigente. Tras cada navegación se comprueba que el Tenant activo sigue siendo el
/// elegido y que no se ha pintado ningún aviso de fin de acceso.
/// </summary>
internal static class RecorridoFichas360
{
    private static readonly LocatorAssertionsToBeVisibleOptions EsperaEnFrio = new() { Timeout = 30_000 };

    public static async Task ComprobarQueElTenantSigueActivoAsync(IPage page, string tenantEsperado, string dondeEstamos)
    {
        if (await Ayudas.DisparadorSelectorTenant(page).CountAsync() > 0)
        {
            await Expect(Ayudas.DisparadorSelectorTenant(page))
                .ToHaveAttributeAsync("data-tenant-id", tenantEsperado, new() { Timeout = 15_000 });
        }
        else
        {
            // Con un único Tenant en su cartera el selector está oculto (decisión 1 del contrato del
            // selector): lo observable es que la cookie de selección sigue puesta, que es justo lo que
            // el aviso de fin de acceso retiraría (se comprueba abajo).
            var cookies = await page.Context.CookiesAsync();
            Assert.Contains(cookies, c => c.Name == "cae_cliente_activo");
        }
        Assert.True(
            await page.Locator(".aviso-fin-de-acceso, .aviso-ventana-soporte").CountAsync() == 0,
            $"En {dondeEstamos} ({page.Url}) se pintó un aviso de fin de acceso: la selección del Tenant beneficiario se retiró.");
    }

    /// <summary>
    /// Lista de Trabajadores → vista previa → «Ver Trabajador 360 →» (navegación con
    /// recarga completa) → Trabajador 360, comprobando el Tenant activo en cada paso.
    /// </summary>
    public static async Task AbrirTrabajador360DesdeLaListaAsync(IPage page, string baseUrl, string tenantEsperado)
    {
        await Ayudas.NavegarYEsperarAsync(page, $"{baseUrl}/trabajadores");
        await ComprobarQueElTenantSigueActivoAsync(page, tenantEsperado, "la lista de Trabajadores");

        await page.WaitForTimeoutAsync(6_000);
        await ComprobarQueElTenantSigueActivoAsync(page, tenantEsperado, "la lista de Trabajadores tras la revalidación del circuito");

        var enlace = page.Locator(".enlace-nombre-fila").First;
        await Expect(enlace).ToBeVisibleAsync(EsperaEnFrio);
        await enlace.ClickAsync();
        await page.GetByText("Ver Trabajador 360 →").ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/trabajadores/[0-9a-f-]{36}"));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        // Barrera de «la ficha cargó»: el contenedor de acciones de la cabecera, que se pinta con cualquier rol.
        // El «⋯» no sirve: sus tres acciones son escrituras y a un rol de solo lectura (el del recorrido delegado) no se le pinta.
        await Expect(page.Locator(".trabajador360-acciones")).ToHaveCountAsync(1, new() { Timeout = 15_000 });
        await ComprobarQueElTenantSigueActivoAsync(page, tenantEsperado, "Trabajador 360");

        // Y la ficha sobrevive a recargarla.
        await page.ReloadAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await ComprobarQueElTenantSigueActivoAsync(page, tenantEsperado, "Trabajador 360 recargada");
    }

    /// <summary>
    /// Lista de Centros → acordeón del primer Centro → «Ver el centro completo →»
    /// (navegación mejorada) → Centro 360, y recarga.
    /// </summary>
    public static async Task AbrirCentro360DesdeLaListaAsync(IPage page, string baseUrl, string tenantEsperado)
    {
        await Ayudas.NavegarYEsperarAsync(page, $"{baseUrl}/centros");
        await ComprobarQueElTenantSigueActivoAsync(page, tenantEsperado, "la lista de Centros");
        await Ayudas.MostrarCentrosSinAgruparAsync(page);

        var expandir = page.Locator("button.boton-expandir-fila").First;
        await Expect(expandir).ToBeVisibleAsync(EsperaEnFrio);
        // El botón llega con el prerender antes que el circuito: un clic en esa ventana se pierde.
        for (var intento = 1; await expandir.GetAttributeAsync("aria-expanded") != "true"; intento++)
        {
            Assert.True(intento <= 10, "El acordeón del Centro no se expandió tras 10 clics.");
            await expandir.ClickAsync();
            await page.WaitForTimeoutAsync(1_000);
        }
        await page.Locator("a.acordeon-centro-enlace-360").First.ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/centros/[0-9a-f-]{36}"));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(page.Locator("main h1")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await ComprobarQueElTenantSigueActivoAsync(page, tenantEsperado, "Centro 360");

        await page.ReloadAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await ComprobarQueElTenantSigueActivoAsync(page, tenantEsperado, "Centro 360 recargada");
    }

    /// <summary>Lista de Empresas → nombre de la primera → Empresa 360, y recarga.</summary>
    public static async Task AbrirEmpresa360DesdeLaListaAsync(IPage page, string baseUrl, string tenantEsperado)
    {
        await Ayudas.NavegarYEsperarAsync(page, $"{baseUrl}/empresas");
        await ComprobarQueElTenantSigueActivoAsync(page, tenantEsperado, "la lista de Empresas");

        var enlace = page.Locator("a.enlace-nombre-fila.celda-empresa").First;
        await Expect(enlace).ToBeVisibleAsync(EsperaEnFrio);
        await enlace.ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/empresas/[0-9a-f-]{36}$"));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await ComprobarQueElTenantSigueActivoAsync(page, tenantEsperado, "Empresa 360");

        await page.ReloadAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await ComprobarQueElTenantSigueActivoAsync(page, tenantEsperado, "Empresa 360 recargada");
    }

    public static async Task RecorrerTodasAsync(IPage page, string baseUrl, string tenantEsperado)
    {
        await AbrirTrabajador360DesdeLaListaAsync(page, baseUrl, tenantEsperado);
        await AbrirCentro360DesdeLaListaAsync(page, baseUrl, tenantEsperado);
        await AbrirEmpresa360DesdeLaListaAsync(page, baseUrl, tenantEsperado);
    }
}
