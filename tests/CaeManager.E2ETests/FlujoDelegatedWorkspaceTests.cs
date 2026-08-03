using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// P1-19 de docs/business/MATURITY_REVIEW.md: uno de los 3 "flujos
/// diferenciadores" (Delegated Workspace, retención, soporte) que no tenían
/// ningún E2E dedicado — AlcanceRolesTests ya usa
/// Ayudas.CambiarClienteActivoAsync como parte de un test de alcance por
/// rol, pero ninguno prueba el propio mecanismo de cambio de "Cliente
/// activo" (SelectorClienteActivo.razor) como flujo en sí: cambiar y volver
/// al origen, con la interfaz reflejando el workspace correcto en cada paso.
/// </summary>
[Collection("AppCollection")]
public class FlujoDelegatedWorkspaceTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Cambiar_al_workspace_delegado_y_volver_al_origen_actualiza_el_selector()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

        // El Administrador inicial arranca en su tenant de origen (la
        // Consultora, ADR-004 § 5.1) — el <select> lo refleja como
        // seleccionado por defecto.
        var origenId = await page.Locator(".selector-cliente-activo").InputValueAsync();
        Assert.False(string.IsNullOrWhiteSpace(origenId));

        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, Ayudas.NombreClienteDelegadoDemo);

        var idDelegado = await page.Locator(".selector-cliente-activo").InputValueAsync();
        Assert.NotEqual(origenId, idDelegado);

        // Locator en vez de EvalOnSelectorAsync a propósito: el <select> acaba
        // de disparar una navegación completa (SelectOptionAsync sobre el
        // <form> de M-8), y un Locator vuelve a resolverse contra el DOM
        // actual en cada acción — EvalOnSelectorAsync no reintenta, así que
        // podía toparse con "Execution context was destroyed" si la
        // navegación no había terminado de sustituir el documento.
        var textoSeleccionado = await page.Locator(".selector-cliente-activo option:checked").TextContentAsync();
        Assert.Equal(Ayudas.NombreClienteDelegadoDemo, textoSeleccionado);

        // Vuelve al tenant de origen — el selector debe reflejar exactamente
        // el mismo Id que al principio, no solo "un tenant distinto del
        // delegado".
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, Ayudas.NombreTenantOrigenPorDefecto);

        var idTrasVolver = await page.Locator(".selector-cliente-activo").InputValueAsync();
        Assert.Equal(origenId, idTrasVolver);
    }

    /// <summary>
    /// El Administrador tiene acceso a dos Clientes Delegante de demo
    /// (DelegacionDemoSeeder) — cambiar entre los dos, sin pasar por el
    /// origen, también debe funcionar: no es solo un toggle binario
    /// origen/delegado.
    /// </summary>
    [Fact]
    public async Task Cambiar_entre_dos_workspaces_delegados_sin_pasar_por_el_origen()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, Ayudas.NombreClienteDelegadoDemo);
        var primerId = await page.Locator(".selector-cliente-activo").InputValueAsync();

        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, Ayudas.NombreClienteDelegadoDemo2);
        var segundoId = await page.Locator(".selector-cliente-activo").InputValueAsync();

        Assert.NotEqual(primerId, segundoId);

        // Locator en vez de EvalOnSelectorAsync a propósito: el <select> acaba
        // de disparar una navegación completa (SelectOptionAsync sobre el
        // <form> de M-8), y un Locator vuelve a resolverse contra el DOM
        // actual en cada acción — EvalOnSelectorAsync no reintenta, así que
        // podía toparse con "Execution context was destroyed" si la
        // navegación no había terminado de sustituir el documento.
        var textoSeleccionado = await page.Locator(".selector-cliente-activo option:checked").TextContentAsync();
        Assert.Equal(Ayudas.NombreClienteDelegadoDemo2, textoSeleccionado);
    }
}
