using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Verificación end-to-end del aislamiento multi-tenant con un navegador
/// real (PLAN-MIGRACION-MULTITENANT.md § 6, Etapa 5) — complementa los
/// tests de integración de aislamiento (AislamientoMultiTenantTests,
/// AislamientoPorAgregadoTests en CaeManager.IntegrationTests), que
/// prueban el filtro de EF Core directamente pero no la ruta real
/// HTTP → claim de sesión → filtro. Dos sesiones de navegador
/// independientes (contextos separados, sin compartir cookies), cada una
/// autenticada como el Administrador de un tenant distinto.
///
/// Las credenciales del segundo tenant están definidas en
/// SegundoTenantSeeder (CaeManager.Infrastructure) — este proyecto de
/// test no referencia Infrastructure (solo Playwright/PDFsharp/xUnit), así
/// que se repiten aquí literalmente; si cambian allí, este test debe
/// actualizarse también.
/// </summary>
[Collection("AppCollectionMultiTenant")]
public class AislamientoMultiTenantE2ETests(WebAppFixtureConSegundoTenant fixture)
{
    private const string EmailAdministradorSegundoTenant = "admin-segundo-tenant@caemanager.local";
    private const string ContrasenaAdministradorSegundoTenant = "SegundoTenant#2026";

    [Fact]
    public async Task Un_cliente_creado_por_el_tenant_A_no_es_visible_para_el_tenant_B()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var razonSocialCliente = $"Cliente Aislamiento E2E {sufijo}";

        // --- Tenant A: crea un Cliente con nombre único ---
        await using (var contextoA = await fixture.Browser.NewContextAsync())
        {
            var paginaA = await contextoA.NewPageAsync();
            await Ayudas.IniciarSesionAsync(paginaA, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

            var drawer = paginaA.Locator(".drawer-panel");
            await Ayudas.NavegarYEsperarAsync(paginaA, $"{fixture.BaseUrl}/clientes");

            // .First: el tenant A (Administrador, Consultora sin datos
            // operativos propios — ver ADR-004 § 5.1) arranca con la lista
            // de Clientes vacía, así que "+ Nuevo cliente" aparece tanto en
            // la cabecera como en el EstadoVacio.
            await paginaA.GetByText("+ Nuevo cliente").First.ClickAsync();
            await drawer.GetByLabel("Razón social").FillAsync(razonSocialCliente);
            await drawer.GetByLabel("CIF", new LocatorGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_999_903));
            await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
            await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

            // Confirmación en el propio tenant A: el Cliente recién creado es visible.
            // Acotado a la tabla — ver nota de más abajo sobre el chip "Búsqueda: "…"".
            await paginaA.GetByPlaceholder("Buscar por nombre…").FillAsync(razonSocialCliente);
            await paginaA.Locator(".tabla-datos").GetByText(razonSocialCliente).WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        }

        // --- Tenant B: contexto de navegador completamente separado (sin cookies compartidas) ---
        await using var contextoB = await fixture.Browser.NewContextAsync();
        var paginaB = await contextoB.NewPageAsync();
        await Ayudas.IniciarSesionAsync(paginaB, fixture.BaseUrl, EmailAdministradorSegundoTenant, ContrasenaAdministradorSegundoTenant);

        await Ayudas.NavegarYEsperarAsync(paginaB, $"{fixture.BaseUrl}/clientes");
        await paginaB.GetByPlaceholder("Buscar por nombre…").FillAsync(razonSocialCliente);

        // Se espera explícitamente a que la búsqueda termine de aplicarse (debounce del
        // grid) y se comprueba que la fila del Cliente del tenant A no aparece —
        // sin esta espera, un simple IsVisible() inmediatamente después del FillAsync
        // podría dar un falso "no visible" solo porque el grid no ha reaccionado todavía.
        // El locator se acota a la tabla (.tabla-datos, docs/ux-audit/02-clientes.md H4):
        // fuera de ella, el chip "Búsqueda: "…"" (Parte 1 § 9) repite el término buscado
        // en pantalla y un GetByText sin acotar coincidiría ahí aunque ninguna fila real
        // exista.
        await paginaB.WaitForTimeoutAsync(500);
        var filaClienteTenantA = paginaB.Locator(".tabla-datos").GetByText(razonSocialCliente);
        Assert.False(await filaClienteTenantA.IsVisibleAsync());
    }

    [Fact]
    public async Task El_listado_de_clientes_del_tenant_B_no_contiene_los_datos_de_prueba_sembrados_para_el_tenant_A()
    {
        // DelegacionDemoSeeder siembra ~200 Clientes en un tenant Cliente
        // Delegante propio (ver ADR-004 § 5.1 — el tenant por defecto/A no
        // tiene datos operativos propios) — el tenant B solo tiene los
        // Clientes que se creen explícitamente en sus propios tests, sea
        // cual sea el tenant de origen de esos datos. Si el filtro global
        // de EF Core fallara, este listado mostraría filas ajenas en vez de
        // estar vacío.
        await using var contextoB = await fixture.Browser.NewContextAsync();
        var paginaB = await contextoB.NewPageAsync();
        await Ayudas.IniciarSesionAsync(paginaB, fixture.BaseUrl, EmailAdministradorSegundoTenant, ContrasenaAdministradorSegundoTenant);

        await Ayudas.NavegarYEsperarAsync(paginaB, $"{fixture.BaseUrl}/clientes");

        // Clientes.razor muestra el estado vacío ("Aún no hay clientes") cuando
        // el total de elementos es 0 (ver Clientes.razor.cs) — si el filtro
        // global fallara y se filtraran Clientes ajenos a este tenant, este
        // estado vacío nunca aparecería.
        await paginaB.GetByText("Aún no hay clientes").WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
    }
}
