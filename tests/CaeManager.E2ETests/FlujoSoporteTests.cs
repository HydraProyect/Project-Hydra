using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// P1-19 de docs/business/MATURITY_REVIEW.md: acceso de soporte (Fase 60) no
/// tenía ningún E2E — solo cobertura de dominio (DelegacionSoporteTests).
/// Cubre el ciclo completo desde /delegaciones: abrir acceso (motivo +
/// ventana), operar el workspace delegado como lo haría quien atiende una
/// incidencia de verdad (seleccionar el Cliente Delegante como Cliente
/// activo y navegar), ver que eso queda en la actividad registrada junto al
/// evento de apertura, y cerrarlo.
///
/// La primera versión de este test solo comprobaba "Acceso concedido"/
/// "Acceso cerrado", que escriben directamente AbrirAccesoSoporteCommand/
/// CerrarAccesoSoporteCommand — nunca ejercitaba TrazaSoporteService (el
/// mecanismo que CLAUDE.md destaca de esta feature: navegación y clics
/// registrados mientras el operador tiene el Cliente Delegante como Cliente
/// activo). Hallazgo de auditoría (revisión de af823ba/840eff7/1f53b53):
/// cubierto ahora seleccionando de verdad el workspace delegado y navegando
/// tras abrir el acceso — se comprueba "Navegó a" en la actividad.
///
/// Fuera de alcance, tras cinco intentos reales en CI: verificar también
/// "Pulsó" (TipoActividadSoporte.Interaccion). El registro de clics vive en
/// trazaSoporte.js — se acumulan en el navegador, se envían por lotes cada
/// 2s, y su listener (document.addEventListener, enganchado en
/// OnAfterRenderAsync tras importar el módulo) no tiene ninguna señal en el
/// DOM que Playwright pueda esperar: cada intento de sincronizarlo con una
/// espera fija distinta (tras el clic, antes del clic, separando navegación
/// de interacción) reprodujo el mismo síntoma — la Interaccion simplemente
/// no llegaba a registrarse en CI, aunque el clic sí abría el drawer
/// correspondiente. El propio mecanismo de captura de clics no tiene un
/// gancho de producción pensado para pruebas (algo tipo "avísame cuando el
/// listener ya esté enganchado"); añadir uno solo para este test no está
/// justificado todavía. Verificar "Navegó a" ya prueba que TrazaSoporteService
/// escribe de verdad mientras se opera el workspace delegado — la brecha que
/// quedaba sin cubrir tras la primera versión del test.
///
/// Solo el Administrador inicial puede abrir acceso de soporte:
/// AbrirAccesoSoporteCommandHandler exige que el tenant de ORIGEN del
/// llamador tenga EsPlataforma=true (solo el tenant #1) y que la cuenta
/// tenga 2FA activo — únicamente Ayudas.EmailAdministrador cumple ambas
/// condiciones entre los usuarios sembrados.
///
/// Colección propia (WebAppFixtureParaSoporte, no "AppCollection"): abrir
/// el acceso deja una AsignacionOperadorDelegado nueva que sobrevive a
/// cerrarlo — ver el comentario de esa clase en WebAppFixture.cs.
///
/// Fuera de alcance a propósito: caducidad/revocación de la ventana de
/// soporte. Ya está cubierta a nivel de dominio (DelegacionSoporteTests,
/// DelegacionTenant.EstaVigente) y provocarla de verdad desde la UI
/// requeriría manipular el reloj del servidor o la base de datos
/// directamente — más una prueba de integración que un E2E de UI.
/// </summary>
[Collection("AppCollectionSoporte")]
public class FlujoSoporteTests(WebAppFixtureParaSoporte fixture)
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

        // --- Operar el workspace delegado de verdad: TrazaSoporteService solo
        // escribe mientras el Cliente Delegante está seleccionado como
        // Cliente activo (ver su comentario de clase) — sin este paso, la
        // actividad registrada solo tendría el evento de apertura, nunca la
        // navegación que es la pieza central de la feature. ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/");
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, Ayudas.NombreClienteDelegadoDemo);

        // El aviso solo se pinta cuando TrazaSoporteService.EsSesionDeSoporteAsync
        // resuelve a true — confirma que la sesión quedó reconocida como de
        // soporte antes de generar la actividad que se comprueba después.
        await page.Locator(".aviso-sesion-soporte").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        // Navegación real a /clientes: Navegacion nueva vía
        // TrazaSoporte.OnInitializedAsync/LocationChanged.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");

        // --- Volver al tenant de origen: gestionar delegaciones (incluida
        // la propia lectura de actividad) se hace desde la organización
        // propia, nunca operando el workspace ajeno (Delegaciones.razor.cs,
        // OperandoWorkspaceAjeno). ---
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, Ayudas.NombreTenantOrigenPorDefecto);

        // Clic en el enlace de navegación, no un GotoAsync directo: el
        // redirect que hace CambiarClienteActivoAsync puede seguir
        // asentándose justo cuando se pediría una navegación nueva de golpe
        // — visto en CI como "net::ERR_ABORTED" al navegar a /delegaciones.
        // Un clic dentro de la propia página ya cargada no compite con esa
        // navegación en curso. Delegaciones vuelve a tener botón propio en
        // el menú (grupo "Plataforma", NavMenu.razor — resuelve la tensión
        // AdminPlataforma de #401/#403: su autoridad es de capacidad, no del
        // rol Administrador que gateaba el hub de Configuración donde vivía
        // antes), así que se entra directamente, sin pasar por el hub.
        await page.Locator(".nav-item", new PageLocatorOptions { HasText = "Delegaciones" }).ClickAsync();
        await page.WaitForURLAsync($"{fixture.BaseUrl}/delegaciones");

        tarjeta = TarjetaSoporte(page, Ayudas.NombreClienteDelegadoDemo);
        await tarjeta.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // --- La actividad registrada incluye la concesión del acceso y la
        // navegación real, no solo el evento de apertura ---
        await tarjeta.GetByText("Ver actividad registrada").ClickAsync();

        var drawer = page.Locator(".drawer-panel").Filter(new LocatorFilterOptions { HasText = "Actividad de soporte registrada" });
        await drawer.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        var tabla = drawer.Locator(".tabla-datos");
        await tabla.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        var textoActividad = await tabla.InnerTextAsync();
        Assert.Contains("Acceso concedido", textoActividad);
        Assert.Contains("Navegó a", textoActividad);

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
