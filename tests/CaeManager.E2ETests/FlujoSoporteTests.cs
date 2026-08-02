using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// P1-19 de docs/business/MATURITY_REVIEW.md: acceso de soporte (Fase 60) no
/// tenía ningún E2E — solo cobertura de dominio (DelegacionSoporteTests).
/// Cubre el ciclo completo desde /delegaciones: abrir acceso (motivo +
/// ventana), ver que queda registrado en la actividad, y cerrarlo — el
/// mismo camino que seguiría un Administrador de plataforma de verdad, no
/// una llamada directa a los Commands.
///
/// Solo el Administrador inicial puede abrir acceso de soporte:
/// AbrirAccesoSoporteCommandHandler exige que el tenant de ORIGEN del
/// llamador tenga EsPlataforma=true (solo el tenant #1) y que la cuenta
/// tenga 2FA activo — únicamente Ayudas.EmailAdministrador cumple ambas
/// condiciones entre los usuarios sembrados.
/// </summary>
[Collection("AppCollection")]
public class FlujoSoporteTests(WebAppFixture fixture)
{
    /// <summary>
    /// Localiza la tarjeta de delegación de Soporte (no la Comercial) hacia
    /// el Cliente Delegante indicado — ambas comparten el mismo título
    /// "Gestionamos a {Cliente}" (SomosLaConsultora=true en las dos), así
    /// que hace falta desambiguar por el badge "Soporte" que solo lleva esa.
    /// </summary>
    private static ILocator TarjetaSoporte(IPage page, string nombreCliente) =>
        page.Locator(".tarjeta-delegacion")
            .Filter(new LocatorFilterOptions { HasText = nombreCliente })
            .Filter(new LocatorFilterOptions { Has = page.Locator(".badge", new PageLocatorOptions { HasText = "Soporte" }) });

    [Fact]
    public async Task Abrir_acceso_de_soporte_lo_registra_en_la_actividad_y_se_puede_cerrar()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/delegaciones");

        var tarjeta = TarjetaSoporte(page, Ayudas.NombreClienteDelegadoDemo);
        await tarjeta.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // --- Abrir acceso ---
        await tarjeta.GetByText("Abrir acceso").ClickAsync();

        var modalAbrir = page.Locator(".modal-contenido").Filter(new LocatorFilterOptions { HasText = "Abrir acceso de soporte" });
        await modalAbrir.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await modalAbrir.GetByLabel("Motivo").FillAsync("E2E: verificación del flujo de soporte (P1-19)");
        // Días de acceso y Permisos se dejan con su valor por defecto (7 días, Solo lectura).
        await modalAbrir.Locator(".modal-pie").GetByText("Abrir acceso").ClickAsync();

        await modalAbrir.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });

        // La tarjeta pasa a "Acceso abierto" y el botón a "Cerrar acceso".
        await tarjeta.GetByText("Acceso abierto").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await tarjeta.GetByText("Cerrar acceso").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        // --- La actividad registrada incluye la concesión del acceso ---
        await tarjeta.GetByText("Ver actividad registrada").ClickAsync();

        var drawer = page.Locator(".drawer-panel").Filter(new LocatorFilterOptions { HasText = "Actividad de soporte registrada" });
        await drawer.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        var tabla = drawer.Locator(".tabla-datos");
        await tabla.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        Assert.Contains("Acceso concedido", await tabla.InnerTextAsync());

        // Cerrar el drawer para poder interactuar de nuevo con la tarjeta —
        // el botón explícito, no Escape: no depende de que dialogo-foco.js
        // ya haya movido el foco dentro del drawer.
        await drawer.Locator(".drawer-cerrar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });

        // --- Cerrar acceso ---
        await tarjeta.GetByText("Cerrar acceso").ClickAsync();

        await tarjeta.GetByText("Abrir acceso").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        // La actividad registrada ahora incluye también el cierre.
        await tarjeta.GetByText("Ver actividad registrada").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        Assert.Contains("Acceso cerrado", await tabla.InnerTextAsync());
    }
}
