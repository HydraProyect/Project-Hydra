using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Propuesta de apoyo de punta a punta, con dos sesiones de dos Gestores CAE del mismo Operador
/// CAE externo: la Gestora CAE principal de un Tenant propone desde «Dar acceso», el otro Gestor
/// CAE —que está trabajando en otro Tenant— la acepta desde la campana y abre el Tenant nuevo
/// por el POST revalidado de <c>/cuenta/cliente-activo</c>.
///
/// <para>
/// <b>Lo que solo esta capa prueba:</b> que la campana enseña la propuesta con otro Tenant activo
/// en la sesión real (cookie de Tenant, circuito, RLS de producción), y que la cartera recién
/// emitida autoriza de verdad el cambio de Tenant y la lectura de sus datos. Las reglas (quién
/// propone, quién acepta, carreras) están en Application.Tests e IntegrationTests.
/// </para>
///
/// <para>
/// Comparte fixture con <see cref="GestorCaeCarteraMultiTenantTests"/> y deja escrito un cambio:
/// el Gestor CAE de apoyo gana cartera en el Tenant A. Ningún otro test de la colección usa esa
/// cuenta. Los nombres duplican los de la siembra a propósito (este proyecto no referencia
/// Infrastructure).
/// </para>
/// </summary>
[Collection("AppCollectionGestorCaeCarteraMultiTenant")]
public class PropuestaApoyoCarteraE2ETests(WebAppFixtureGestorCaeCarteraMultiTenant fixture)
{
    private const string EmailGestoraPrincipal = "gestor.cartera.e2e@caemanager.local";
    private const string EmailGestorDeApoyo = "gestor.apoyo.e2e@caemanager.local";
    private const string NombreGestorDeApoyo = "Iván Roca (Gestor CAE de apoyo, E2E)";

    private const string TenantPropuesto = "Conservas Albatros S.L. (Tenant beneficiario E2E)";
    private const string TrabajadorDelTenantPropuesto = "Castany Olmo";
    private const string TrabajadorDelTenantActivo = "Ledesma Pardo";

    private static readonly LocatorAssertionsToBeVisibleOptions EsperaEnFrio = new() { Timeout = 30_000 };

    [Fact]
    public async Task La_principal_propone_el_apoyo_y_el_otro_Gestor_CAE_lo_acepta_desde_la_campana_con_otro_Tenant_activo_y_abre_el_Tenant()
    {
        var tenantPropuesto = await fixture.LeerValorSqlAsync(
            """SELECT "Id"::text FROM "Tenants" WHERE "Nombre" = @n""", ("n", TenantPropuesto));

        // ── A: la Gestora CAE principal propone desde «Dar acceso» ──
        await using (var contextoPrincipal = await fixture.Browser.NewContextAsync())
        {
            var page = await contextoPrincipal.NewPageAsync();
            await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, EmailGestoraPrincipal, Ayudas.ContrasenaUsuariosPrueba);
            await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/cartera/solicitudes");

            var fila = page.Locator("[data-dar-acceso-operacion]", new() { HasText = TenantPropuesto });
            await Expect(fila).ToBeVisibleAsync(EsperaEnFrio);
            await fila.Locator("> button").ClickAsync();

            var desplegable = page.Locator("select").Filter(new() { Has = page.Locator("option", new() { HasText = "Elige un Gestor CAE" }) });
            await Expect(desplegable).ToBeVisibleAsync(EsperaEnFrio);
            await desplegable.SelectOptionAsync(new SelectOptionValue { Label = NombreGestorDeApoyo });
            await page.GetByRole(AriaRole.Button, new() { Name = "Proponer", Exact = true }).ClickAsync();

            await Expect(fila.Locator("[data-propuesta-enviada]")).ToContainTextAsync("sin responder todavía", new() { Timeout = 30_000 });
        }

        // ── B: el otro Gestor CAE, trabajando en otro Tenant, acepta desde la campana ──
        await using var contextoApoyo = await fixture.Browser.NewContextAsync();
        var pagina = await contextoApoyo.NewPageAsync();
        await Ayudas.IniciarSesionAsync(pagina, fixture.BaseUrl, EmailGestorDeApoyo, Ayudas.ContrasenaUsuariosPrueba);

        // Control: su Tenant activo es otro. Ve al Trabajador de ese Tenant y no al del propuesto.
        await Ayudas.NavegarYEsperarAsync(pagina, $"{fixture.BaseUrl}/trabajadores?q={TrabajadorDelTenantActivo}");
        await Expect(FilaDeTrabajador(pagina, TrabajadorDelTenantActivo)).ToHaveCountAsync(1, new() { Timeout = 30_000 });
        await Ayudas.NavegarYEsperarAsync(pagina, $"{fixture.BaseUrl}/trabajadores?q={TrabajadorDelTenantPropuesto}");
        await Expect(pagina.Locator(".estado-vacio")).ToBeVisibleAsync(EsperaEnFrio);
        await Expect(FilaDeTrabajador(pagina, TrabajadorDelTenantPropuesto)).ToHaveCountAsync(0);

        await pagina.Locator("button.campana-boton").ClickAsync();
        var propuesta = pagina.Locator("#panel-campana-avisos [data-propuesta-apoyo]");
        await Expect(propuesta).ToBeVisibleAsync(EsperaEnFrio);
        await Expect(propuesta).ToContainTextAsync($"te propone apoyo en {TenantPropuesto}");

        await propuesta.Locator("button", new() { HasText = "Aceptar" }).ClickAsync();

        var aceptada = pagina.Locator("#panel-campana-avisos [data-propuesta-aceptada]");
        await Expect(aceptada).ToBeVisibleAsync(EsperaEnFrio);
        await Expect(pagina.Locator("#panel-campana-avisos [data-propuesta-apoyo]")).ToHaveCountAsync(0);

        // Lo que la aceptación escribió: una cartera viva, sin la marca de principal y de rol Gestor CAE.
        Assert.Equal("false/GestorCae", await fixture.LeerValorSqlAsync(
            """
            SELECT string_agg(c."EsPrincipal"::text || '/' || c."Rol", ',')
            FROM "AsignacionesCartera" c
            JOIN "AspNetUsers" u ON u."Id" = c."UsuarioId"
            WHERE u."Email" = @e AND c."PropietarioTenantId"::text = @t
            """, ("e", EmailGestorDeApoyo), ("t", tenantPropuesto)));

        // ── B abre el Tenant: el POST revalidado, no un atajo ──
        var respuesta = await pagina.RunAndWaitForResponseAsync(
            () => aceptada.Locator("button[type=submit]").ClickAsync(),
            r => r.Url.Contains("/cuenta/cliente-activo") && r.Request.Method == "POST");
        Assert.True(respuesta.Status is >= 300 and < 400,
            $"POST a /cuenta/cliente-activo devolvió {respuesta.Status} al abrir «{TenantPropuesto}».");
        var destino = respuesta.Headers.GetValueOrDefault("location") ?? string.Empty;
        Assert.False(destino.Contains("acceso-denegado") || destino.Contains("iniciar-sesion"),
            $"El POST a /cuenta/cliente-activo redirigió a «{destino}»: la cartera recién aceptada no autorizó abrir «{TenantPropuesto}».");
        await pagina.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(Ayudas.DisparadorSelectorTenant(pagina)).ToHaveAttributeAsync("data-tenant-id", tenantPropuesto, new() { Timeout = 30_000 });
        await Ayudas.NavegarYEsperarAsync(pagina, $"{fixture.BaseUrl}/trabajadores?q={TrabajadorDelTenantPropuesto}");
        await Expect(FilaDeTrabajador(pagina, TrabajadorDelTenantPropuesto)).ToHaveCountAsync(1, new() { Timeout = 30_000 });
    }

    private static ILocator FilaDeTrabajador(IPage page, string apellidos) =>
        page.Locator(".tabla-datos").GetByRole(AriaRole.Row, new() { Name = apellidos });
}
