using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Cuando termina una ventana de acceso de Soporte (<c>DelegacionTenant</c> de
/// Soporte, 1-4 h) el usuario tiene que enterarse: antes, un circuito abierto
/// seguía pintando el tenant visitado y, tras recargar, el selector volvía a
/// «Organización principal» sin aviso ni error (medido en local 2026-09-21).
///
/// <para>
/// Ejerce la aplicación entera porque el aviso lo componen tres piezas que
/// solo juntas se ven: el middleware de revalidación (que retira la selección),
/// el evento hacia el circuito y el componente estático que pinta la respuesta.
/// La ventana se «hace caducar» moviendo <c>ExpiraEnUtc</c> en la base: es lo
/// único que cambia con el tiempo, y esperar horas no cabe en una suite.
/// </para>
/// </summary>
[Collection("AppCollectionVentanaSoporte")]
public class VentanaDeSoporteTests(WebAppFixtureVentanaSoporte fixture) : IAsyncLifetime
{
    // Cada test abre su propia ventana; la fixture (y su base) es compartida,
    // así que se devuelve la delegación de Soporte a su estado de origen
    // (inactiva, sin caducidad) antes de cada uno.
    public async Task InitializeAsync() => await fixture.EjecutarSqlAsync(
        "UPDATE \"DelegacionesTenant\" SET \"Activa\" = false, \"ExpiraEnUtc\" = NULL WHERE \"Proposito\" = 'Soporte'");

    public Task DisposeAsync() => Task.CompletedTask;

    private const string SqlExpirarSoporte =
        "UPDATE \"DelegacionesTenant\" SET \"ExpiraEnUtc\" = now() - interval '1 minute' " +
        "WHERE \"Proposito\" = 'Soporte' AND \"Activa\" = true";

    private const string SqlCaducarEnCincoMinutos =
        "UPDATE \"DelegacionesTenant\" SET \"ExpiraEnUtc\" = now() + interval '5 minutes' " +
        "WHERE \"Proposito\" = 'Soporte' AND \"Activa\" = true";

    /// <summary>Abre la ventana de soporte desde la UI, como el flujo real, y devuelve la página ya sobre el tenant visitado.</summary>
    private async Task<(IBrowserContext Contexto, IPage Pagina)> AbrirVentanaYEntrarAsync(string sqlAntesDeEntrar)
    {
        var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/delegaciones");

        var tarjeta = page.Locator(".delegaciones-tarjeta")
            .Filter(new LocatorFilterOptions { HasText = Ayudas.NombreClienteDelegadoDemo })
            .Filter(new LocatorFilterOptions { Has = page.GetByText("Soporte", new PageGetByTextOptions { Exact = true }) });
        await tarjeta.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        await tarjeta.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Abrir acceso", Exact = true }).ClickAsync();
        var modal = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Abrir acceso de soporte", Exact = true });
        await modal.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await modal.GetByLabel("Motivo", new LocatorGetByLabelOptions { Exact = true }).FillAsync("E2E: ventana de soporte");
        await modal.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Abrir acceso", Exact = true }).ClickAsync();
        await modal.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });
        await tarjeta.GetByText("Acceso abierto", new LocatorGetByTextOptions { Exact = true })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        Assert.True(await fixture.EjecutarSqlAsync(sqlAntesDeEntrar) >= 1, "la delegación de soporte abierta debe existir en la base");

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/");
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, Ayudas.NombreClienteDelegadoDemo);
        await page.Locator(".aviso-sesion-soporte").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        // El circuito interactivo arranca después del HTML y, en una instancia
        // recién levantada, puede tardar más que el resto del test: el aviso
        // «terminó» de un circuito ya abierto solo se puede observar si los
        // componentes ya están vivos en él. TrazaSoporte importa su módulo JS
        // en su primer render interactivo, y comparte con AvisoVentanaSoporte
        // la misma resolución de la sesión, así que esa petición es la señal de
        // que ya se han suscrito. Un WebSocket conectado no basta: los
        // componentes pueden inicializarse después y leer ya el estado
        // caducado, que ejercería otro camino.
        var moduloImportado = page.WaitForRequestAsync(
            peticion => peticion.Url.Contains("trazaSoporte", StringComparison.OrdinalIgnoreCase),
            new PageWaitForRequestOptions { Timeout = 30_000 });
        await page.ReloadAsync();
        await moduloImportado;

        return (contexto, page);
    }

    [Fact]
    public async Task Al_caducar_la_ventana_un_circuito_abierto_lo_dice_sin_recargar()
    {
        var (contexto, page) = await AbrirVentanaYEntrarAsync(SqlCaducarEnCincoMinutos);
        await using var _ = contexto;

        // Antes de caducar: aviso de antelación, no el de «terminó».
        await Assertions.Expect(page.Locator(".aviso-ventana-soporte"))
            .ToContainTextAsync("La ventana de soporte termina en", new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });
        await Assertions.Expect(page.Locator(".aviso-ventana-soporte--terminada")).ToHaveCountAsync(0);

        // Caduca en la base y NO se recarga: lo tiene que notar el circuito.
        Assert.True(await fixture.EjecutarSqlAsync(SqlExpirarSoporte) >= 1);

        await Assertions.Expect(page.Locator(".aviso-ventana-soporte--terminada"))
            .ToContainTextAsync("La ventana de soporte terminó", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
    }

    [Fact]
    public async Task Al_recargar_tras_caducar_la_ventana_se_explica_el_regreso_a_la_organizacion_principal()
    {
        var (contexto, page) = await AbrirVentanaYEntrarAsync(SqlCaducarEnCincoMinutos);
        await using var _ = contexto;

        Assert.True(await fixture.EjecutarSqlAsync(SqlExpirarSoporte) >= 1);

        await page.GotoAsync($"{fixture.BaseUrl}/clientes");

        await Assertions.Expect(page.Locator(".aviso-fin-de-acceso"))
            .ToContainTextAsync("La ventana de soporte terminó", new LocatorAssertionsToContainTextOptions { Timeout = 15_000 });

        // Y el aviso es de una sola vez: la siguiente carga ya no lo trae.
        await page.GotoAsync($"{fixture.BaseUrl}/clientes");
        await Assertions.Expect(page.Locator(".aviso-fin-de-acceso")).ToHaveCountAsync(0);
    }
}
