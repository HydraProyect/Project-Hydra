using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Mi trabajo Gen2 (/mi-trabajo): la cola agregada de toda la cartera. Fija
/// las dos propiedades que bUnit no puede ver: que el menú lateral solo
/// apunta ahí con más de una organización autorizada, y que la acción de
/// una fila hace el POST real a /cuenta/cliente-activo y aterriza en la
/// pantalla del ítem con la organización de esa fila ya activa.
/// </summary>
[Collection("AppCollection")]
public class MiTrabajoGen2Tests(WebAppFixture fixture)
{
    private static ILocator EnlaceMiTrabajo(IPage page) =>
        page.Locator("nav a.nav-item", new PageLocatorOptions { HasText = "Mi trabajo" });

    [Fact]
    public async Task Con_varias_organizaciones_el_menu_lleva_a_la_cola_de_toda_la_cartera()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministradorConsultora, Ayudas.ContrasenaUsuariosPrueba);

        await Ayudas.AbrirSelectorTenantAsync(page);
        Assert.True(await Ayudas.OpcionesSelectorTenant(page).CountAsync() > 1,
            "precondición: el Administrador de la Consultora debe tener más de una organización autorizada");
        await Ayudas.CerrarSelectorTenantAsync(page);
        await Expect(EnlaceMiTrabajo(page)).ToHaveAttributeAsync("href", "mi-trabajo");
    }

    [Fact]
    public async Task Con_una_sola_organizacion_el_menu_sigue_yendo_a_la_bandeja()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailGestorRefrielectric, Ayudas.ContrasenaUsuariosPrueba);

        await Expect(EnlaceMiTrabajo(page)).ToHaveAttributeAsync("href", "bandeja");
    }

    /// <summary>
    /// Lo que AccionCrossTenant necesita del servidor: un POST del navegador con
    /// antiforgery y un returnUrl profundo, con query, aterriza en esa pantalla
    /// exacta con la organización ya activa (contrato § 8). Que el componente
    /// emita ese formulario lo fijan los tests bUnit de Mi trabajo Gen2. Aquí no
    /// se pulsa una fila real porque la siembra E2E no tiene ningún Gestor CAE
    /// con cartera en varias organizaciones: el Administrador opera las
    /// delegadas con alcance cero por diseño (AlcanceRolesTests), y la siembra
    /// de Dirección, que sí lo tiene, está apagada en E2E a propósito.
    /// </summary>
    [Fact]
    public async Task El_POST_cross_Tenant_con_returnUrl_profundo_aterriza_en_esa_pantalla_con_la_organizacion_activa()
    {
        const string destino = "/documentos?pestana=plataforma";
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministradorConsultora, Ayudas.ContrasenaUsuariosPrueba);

        var tenantDexter = await Ayudas.IdDeOpcionSelectorTenantAsync(page, Ayudas.NombreClienteDelegadoDemo);

        // El token se lee con el locator, que reintenta si un re-render de
        // Blazor desprende el nodo, y no con querySelector dentro del script:
        // entre la espera y la evaluación, ese re-render dejaba el nodo en
        // null (CI de #792, «Cannot read properties of null (reading 'name')»).
        var token = await Ayudas.TokenAntiforgeryAsync(page);

        // Mismo formulario que emite AccionCrossTenant, pero creado fuera del
        // árbol de Blazor con el token de antiforgery de la página: sobre el
        // formulario del selector, un re-render en mitad del paso lo sustituía
        // y el envío se perdía (sin POST) o salía con el returnUrl original.
        var respuesta = await page.RunAndWaitForResponseAsync(
            () => page.EvaluateAsync(
                """
                ([destino, tenant, token]) => {
                    const form = document.createElement("form");
                    form.method = "post";
                    form.action = "/cuenta/cliente-activo";
                    for (const [nombre, valor] of [["__RequestVerificationToken", token], ["tenantId", tenant], ["returnUrl", destino]]) {
                        const campo = document.createElement("input");
                        campo.type = "hidden";
                        campo.name = nombre;
                        campo.value = valor;
                        form.appendChild(campo);
                    }
                    document.body.appendChild(form);
                    form.submit();
                }
                """, new[] { destino, tenantDexter!, token! }),
            r => r.Url.Contains("/cuenta/cliente-activo") && r.Request.Method == "POST");
        Assert.InRange(respuesta.Status, 300, 399);
        Assert.Equal(destino, respuesta.Headers.GetValueOrDefault("location"));

        // La respuesta 302 llega antes de que el navegador confirme la
        // navegación a su Location: NetworkIdle a secas se resolvía sobre el
        // documento de partida, ya en reposo, y page.Url seguía siendo «/»
        // (CI de main, run 37821761480: el test falló 77 ms antes de que el
        // servidor respondiera el GET /documentos, con 200). Primero la URL
        // exacta, después el reposo, y la URL se vuelve a afirmar al final
        // para que una redirección posterior del destino no pase inadvertida.
        await page.WaitForURLAsync(url => new Uri(url).PathAndQuery == destino);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        Assert.Equal(destino, new Uri(page.Url).PathAndQuery);
        await Expect(Ayudas.DisparadorSelectorTenant(page)).ToHaveAttributeAsync("data-tenant-id", tenantDexter!);
    }
}
