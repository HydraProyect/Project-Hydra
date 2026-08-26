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

    // F3b (2026-08-26): estos dos tests verificaban aislamiento a través de
    // /clientes, respaldado por ObtenerClientesQuery — una de las 6 consultas
    // que D2 deja leyendo la tabla legacy Clientes hasta F4. Con los
    // escritores de Cliente redirigidos a Empresa, /clientes queda vacío en
    // cualquier entorno para cualquier tenant (decisión explícita: "aceptar
    // el vacío", ver f3b-decision-d2-transicion-acotada-2026-08-25.md) — una
    // aserción de "no visible"/"vacío" sobre esa pantalla pasaría siempre,
    // aislamiento aparte, y dejaría de probar nada. Se reancla a /empresas
    // (ObtenerEmpresasQuery, sin congelar) para seguir probando el filtro
    // global de EF Core end-to-end con datos reales: el alta de Cliente hoy
    // crea una Empresa con EsCritico != null, que sí aparece ahí.
    [Fact]
    public async Task Una_empresa_creada_por_el_tenant_A_no_es_visible_para_el_tenant_B()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var razonSocialEmpresa = $"Empresa Aislamiento E2E {sufijo}";

        // --- Tenant A: crea una Empresa con nombre único ---
        await using (var contextoA = await fixture.Browser.NewContextAsync())
        {
            var paginaA = await contextoA.NewPageAsync();
            await Ayudas.IniciarSesionAsync(paginaA, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

            var drawer = paginaA.Locator(".drawer-panel");
            await Ayudas.NavegarYEsperarAsync(paginaA, $"{fixture.BaseUrl}/empresas");

            // .First: el tenant A (Administrador, Consultora sin datos
            // operativos propios — ver ADR-004 § 5.1) arranca con la lista
            // de Empresas vacía, así que "+ Nueva empresa" aparece tanto en
            // la cabecera como en el EstadoVacio.
            await paginaA.GetByText("+ Nueva empresa").First.ClickAsync();
            await drawer.GetByLabel("Razón social").FillAsync(razonSocialEmpresa);
            await drawer.GetByLabel("CIF", new LocatorGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_999_903));
            await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
            await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

            // Confirmación en el propio tenant A: la Empresa recién creada es visible.
            // Acotado a la lista — ver nota de más abajo sobre el chip de búsqueda.
            await paginaA.GetByPlaceholder("Buscar por razón social…").FillAsync(razonSocialEmpresa);
            await paginaA.Locator(".lista-filas-acordeon").GetByText(razonSocialEmpresa).WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        }

        // --- Tenant B: contexto de navegador completamente separado (sin cookies compartidas) ---
        await using var contextoB = await fixture.Browser.NewContextAsync();
        var paginaB = await contextoB.NewPageAsync();
        await Ayudas.IniciarSesionAsync(paginaB, fixture.BaseUrl, EmailAdministradorSegundoTenant, ContrasenaAdministradorSegundoTenant);

        await Ayudas.NavegarYEsperarAsync(paginaB, $"{fixture.BaseUrl}/empresas");
        await paginaB.GetByPlaceholder("Buscar por razón social…").FillAsync(razonSocialEmpresa);

        // Se espera explícitamente a que la búsqueda termine de aplicarse (debounce de
        // CampoTexto) y se comprueba que la fila de la Empresa del tenant A no aparece —
        // sin esta espera, un simple IsVisible() inmediatamente después del FillAsync
        // podría dar un falso "no visible" solo porque la lista no ha reaccionado todavía.
        // El locator se acota a la lista (.lista-filas-acordeon): fuera de ella, un chip
        // o mensaje que repita el término buscado en pantalla podría coincidir aunque
        // ninguna fila real exista.
        await paginaB.WaitForTimeoutAsync(500);
        var filaEmpresaTenantA = paginaB.Locator(".lista-filas-acordeon").GetByText(razonSocialEmpresa);
        Assert.False(await filaEmpresaTenantA.IsVisibleAsync());
    }

    [Fact]
    public async Task El_listado_de_empresas_del_tenant_B_no_contiene_los_datos_de_prueba_sembrados_para_el_tenant_A()
    {
        // DelegacionDemoSeeder siembra ~200 Empresas-Cliente en un tenant
        // Cliente Delegante propio (ver ADR-004 § 5.1 — el tenant por
        // defecto/A no tiene datos operativos propios) — el tenant B no
        // recibe ninguna Empresa en su sembrado (SegundoTenantSeeder solo crea
        // Tenant + ParametroSistema + TiposDocumento + un usuario). Si el
        // filtro global de EF Core fallara, este listado mostraría filas
        // ajenas en vez de estar vacío.
        await using var contextoB = await fixture.Browser.NewContextAsync();
        var paginaB = await contextoB.NewPageAsync();
        await Ayudas.IniciarSesionAsync(paginaB, fixture.BaseUrl, EmailAdministradorSegundoTenant, ContrasenaAdministradorSegundoTenant);

        await Ayudas.NavegarYEsperarAsync(paginaB, $"{fixture.BaseUrl}/empresas");

        // Empresas.razor muestra el estado vacío ("Aún no hay empresas") cuando
        // el total de elementos es 0 (ver Empresas.razor.cs) — si el filtro
        // global fallara y se filtraran Empresas ajenas a este tenant, este
        // estado vacío nunca aparecería.
        await paginaB.GetByText("Aún no hay empresas").WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
    }
}
