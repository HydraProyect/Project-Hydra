using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// «Asumir» de punta a punta (ADR-011 § 2.7, enmienda 2026-10-08, punto 4): el Coordinador CAE del
/// Operador CAE externo de la siembra de escenarios ve en su bandeja de cartera una empresa sin
/// principal, la asume con un clic y la abre desde el selector.
///
/// <para>
/// Lo que NO prueba: que antes de asumir no pudiera abrirla. En esta siembra el Coordinador CAE
/// ya alcanza cada Tenant beneficiario por delegación heredada; que sin cartera vigente el
/// alcance es cero lo prueba <c>PrincipalDeCarteraBajoRuntimeTests</c> en integración.
/// </para>
/// </summary>
[Collection("AppCollectionEscenariosDireccion")]
public class AsumirPrincipalDeOperacionE2ETests(WebAppFixtureEscenariosDireccion fixture)
{
    private const string PizzaPlanet = "Pizza Planet S.L.";

    [Fact]
    public async Task Un_Coordinador_CAE_asume_una_empresa_sin_principal_y_la_abre()
    {
        var email = await fixture.LeerValorSqlAsync(
            """SELECT "Email" FROM "AspNetUsers" WHERE "Email" LIKE 'coordinador1.%@caemanager.local' """);
        var tenantPizza = await fixture.LeerValorSqlAsync(
            """SELECT "Id"::text FROM "Tenants" WHERE "Nombre" = @n""", ("n", PizzaPlanet));
        var operacion = await fixture.LeerValorSqlAsync(
            """SELECT "Id"::text FROM "AsignacionesOperacion" WHERE "PropietarioTenantId" = @t::uuid AND NOT "EsRaiz" """,
            ("t", tenantPizza));

        // Premisa de la siembra: nadie lleva la marca de principal en esa empresa.
        Assert.Equal("0", await PrincipalesAsync(tenantPizza));

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, email, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/cartera/solicitudes");

        var sinPrincipal = page.Locator($"table[data-sin-principal] tr[data-operacion='{operacion}']");
        await Expect(sinPrincipal).ToContainTextAsync(PizzaPlanet, new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Expect(sinPrincipal).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });

        await sinPrincipal.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Asumir", Exact = true }).ClickAsync();

        // La fila sale de «sin principal» y pasa a la lista informativa, a nombre de quien mira.
        var conCoordinador = page.Locator($"table[data-coordinador-principal] tr[data-operacion='{operacion}']");
        await Expect(conCoordinador).ToContainTextAsync("Tú", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Expect(sinPrincipal).ToHaveCountAsync(0);
        await Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();

        // En la base: un solo principal, él, con cartera de rol Coordinador CAE.
        Assert.Equal("1", await PrincipalesAsync(tenantPizza));
        Assert.Equal($"{email}|CoordinadorCae", await fixture.LeerValorSqlAsync(
            """
            SELECT u."Email" || '|' || c."Rol"
            FROM "AsignacionesCartera" c JOIN "AspNetUsers" u ON u."Id" = c."UsuarioId"
            WHERE c."PropietarioTenantId" = @t::uuid AND c."EsPrincipal"
            """, ("t", tenantPizza)));

        // Y la abre.
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, PizzaPlanet);
        await RecorridoFichas360.ComprobarQueElTenantSigueActivoAsync(page, tenantPizza, "tras asumir Pizza Planet");
    }

    private Task<string> PrincipalesAsync(string tenantId) =>
        fixture.LeerValorSqlAsync(
            """SELECT count(*)::text FROM "AsignacionesCartera" WHERE "PropietarioTenantId" = @t::uuid AND "EsPrincipal" """,
            ("t", tenantId));
}
