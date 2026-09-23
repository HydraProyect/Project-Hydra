using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Incremento 1b del aprovisionamiento: el Administrador de un Tenant propietario que
/// YA existe autoriza a un Operador CAE externo que el Actor de Plataforma TALVEG le
/// sugiere por enlace. TALVEG solo preselecciona; la única escritura es el clic del
/// Administrador (<c>CrearDelegacionTenantCommand</c>).
///
/// <para>
/// Dos contextos de navegador sin cookies compartidas: el Actor de Plataforma
/// (<see cref="Ayudas.EmailAdministrador"/>, tras el acto fundacional) da de alta un
/// Operador CAE externo nuevo y copia su enlace de autorización; el Administrador del
/// Tenant de verificación B (<c>SegundoTenantSeeder</c>, sin delegaciones propias) lo
/// abre, autoriza y revoca. Operador nuevo por ejecución y revocación al final: una
/// segunda ejecución contra la misma base no choca con un Operador vigente.
/// </para>
/// </summary>
[Collection("AppCollectionMultiTenant")]
public class FlujoAutorizacionOperadorCaeExternoTests(WebAppFixtureConSegundoTenant fixture)
{
    private const string EmailAdministradorSegundoTenant = "admin-segundo-tenant@caemanager.local";
    private const string ContrasenaAdministradorSegundoTenant = "SegundoTenant#2026";

    [Fact]
    public async Task El_Administrador_del_Tenant_propietario_autoriza_el_Operador_sugerido_por_TALVEG_y_lo_revoca()
    {
        var nombreOperador = $"Operador CAE E2E {Guid.NewGuid().ToString("N")[..8]}";

        // --- Actor de Plataforma TALVEG: alta del Operador y enlace de autorización ---
        await using var contextoPlataforma = await fixture.Browser.NewContextAsync();
        await contextoPlataforma.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        var plataforma = await contextoPlataforma.NewPageAsync();
        await Ayudas.IniciarSesionAsync(plataforma, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
        await CruzarActoFundacionalAsync(plataforma);

        await Ayudas.NavegarYEsperarAsync(plataforma, $"{fixture.BaseUrl}/delegaciones");
        await plataforma.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Nuevo Operador CAE externo", Exact = true }).ClickAsync();
        var modalOperador = plataforma.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Nuevo Operador CAE externo", Exact = true });
        await modalOperador.GetByLabel("Nombre del Operador CAE externo", new LocatorGetByLabelOptions { Exact = true }).FillAsync(nombreOperador);
        await modalOperador.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Crear", Exact = true }).ClickAsync();
        await modalOperador.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        var tarjetaOperador = plataforma.Locator(".operadores-cae-tarjeta", new PageLocatorOptions { HasText = nombreOperador });
        await tarjetaOperador.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Copiar enlace de autorización" }).ClickAsync();
        // El toast de BotonCopiar es la barrera: la escritura en el portapapeles es un
        // JSInterop asíncrono y leerlo justo tras el clic puede adelantarse (DeepLinksTests).
        await plataforma.GetByText("Se copió el enlace de autorización al portapapeles.").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        var enlace = await plataforma.EvaluateAsync<string>("navigator.clipboard.readText()");
        Assert.Contains("/delegaciones?autorizar=", enlace);

        // --- Administrador del Tenant propietario B: abre el enlace, autoriza, revoca ---
        await using var contextoB = await fixture.Browser.NewContextAsync();
        var paginaB = await contextoB.NewPageAsync();
        await Ayudas.IniciarSesionAsync(paginaB, fixture.BaseUrl, EmailAdministradorSegundoTenant, ContrasenaAdministradorSegundoTenant);
        await Ayudas.NavegarYEsperarAsync(paginaB, enlace);

        var modalAutorizar = paginaB.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Autorizar un Operador CAE externo", Exact = true });
        await modalAutorizar.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        var candidato = modalAutorizar.GetByRole(AriaRole.Option, new LocatorGetByRoleOptions { Name = nombreOperador });
        await Expect(candidato).ToHaveAttributeAsync("aria-selected", "true", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });
        await Expect(modalAutorizar).ToContainTextAsync("TALVEG te sugiere este Operador CAE externo");

        // Sugerir no es autorizar: hasta el clic, el Tenant B no tiene ningún vínculo.
        var tarjetaVinculo = paginaB.Locator(".delegaciones-tarjeta", new PageLocatorOptions { HasText = nombreOperador });
        Assert.Equal(0, await tarjetaVinculo.CountAsync());

        await modalAutorizar.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Autorizar", Exact = true }).ClickAsync();
        await modalAutorizar.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        await tarjetaVinculo.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await Expect(tarjetaVinculo).ToContainTextAsync("Activa");

        await tarjetaVinculo.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Revocar acceso", Exact = true }).ClickAsync();
        var modalRevocar = paginaB.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Revocar el acceso", Exact = true });
        await modalRevocar.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Revocar acceso", Exact = true }).ClickAsync();
        await modalRevocar.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
        await Expect(tarjetaVinculo).ToContainTextAsync("Revocada");
    }

    /// <summary>
    /// Mismo acto que <c>FlujoAltaYRevocacionDelegacionTests</c>: la concesión
    /// AdminPlataforma solo nace cruzando esta puerta. Tolera encontrarla ya cruzada.
    /// </summary>
    private async Task CruzarActoFundacionalAsync(IPage page)
    {
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/configuracion/plataforma");
        var puerta = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Inicializar administración global" });
        var yaInicializada = page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "No hay nada que inicializar aquí" });
        await puerta.Or(yaInicializada).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        if (await puerta.CountAsync() > 0)
        {
            await puerta.ClickAsync();
            var confirmacion = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Confirmar el acto fundacional" });
            await confirmacion.GetByLabel(
                "Entiendo que este paso se ejecuta una sola vez y no vuelve a estar disponible.").CheckAsync();
            await confirmacion.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Sí, inicializar" }).ClickAsync();
            await Expect(yaInicializada).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        }
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
