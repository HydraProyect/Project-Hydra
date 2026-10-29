using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// P1-19 de Project-Hydra-Negocio/MATURITY_REVIEW.md: acceso de soporte (Fase 60) no
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
/// La captura de clics y la confirmación del lote registrado se observan en
/// el aviso de soporte antes de cambiar de Tenant y destruir el circuito.
/// El Actor real sigue siendo el Actor de Plataforma TALVEG autenticado;
/// este recorrido de la vía heredada no selecciona ningún Usuario simulado.
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
    /// que se desambigua por el botón de actividad que solo lleva la de soporte.
    /// </summary>
    private static ILocator TarjetaSoporte(IPage page, string nombreCliente) =>
        page.Locator(".delegaciones-tarjeta")
            .Filter(new LocatorFilterOptions { Has = page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = nombreCliente }) })
            .Filter(new LocatorFilterOptions { Has = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Ver actividad registrada", Exact = true }) });

    [Fact]
    public async Task Abrir_acceso_de_soporte_lo_registra_en_la_actividad_y_se_puede_cerrar()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/delegaciones");

        await Ayudas.AbrirDetalleDeDelegacionAsync(page, Ayudas.NombreClienteDelegadoDemo, soporte: true);
        var tarjeta = TarjetaSoporte(page, Ayudas.NombreClienteDelegadoDemo);
        await tarjeta.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // --- Abrir acceso ---
        await tarjeta.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Abrir acceso", Exact = true }).ClickAsync();

        var modalAbrir = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Abrir acceso de soporte", Exact = true });
        await modalAbrir.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await modalAbrir.GetByLabel("Motivo", new LocatorGetByLabelOptions { Exact = true }).FillAsync("E2E: verificación del flujo de soporte (P1-19)");
        // Horas de acceso y Permisos se dejan con su valor por defecto (4 horas, Solo lectura).
        await modalAbrir.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Abrir acceso", Exact = true }).ClickAsync();

        await modalAbrir.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });

        // La tarjeta pasa a "Acceso abierto" y el botón a "Cerrar acceso".
        await tarjeta.GetByText("Acceso abierto", new LocatorGetByTextOptions { Exact = true }).WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await tarjeta.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Cerrar acceso", Exact = true }).WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

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

        // Navegación real a /clientes: documento nuevo, así que la Navegacion
        // la anota TrazaSoporte.OnInitializedAsync (la primera pantalla).
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");

        // El aviso se pinta antes de que OnAfterRenderAsync importe trazaSoporte.js:
        // un clic en ese hueco no lo captura nadie. Se espera al listener
        // (data-captura-lista) y, tras el clic, al lote ya guardado
        // (data-interacciones-registradas), porque el cambio de Tenant de abajo
        // recarga el documento y se llevaría el lote pendiente.
        var aviso = page.Locator(".aviso-sesion-soporte");
        await Assertions.Expect(aviso).ToHaveAttributeAsync("data-captura-lista", "true",
            new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });
        var registradas = await aviso.GetAttributeAsync("data-interacciones-registradas");
        // Una interacción sin navegación: el documento que confirma el lote es el
        // mismo que capturó el clic.
        await Ayudas.AbrirSelectorTenantAsync(page);
        // El lote sale cada 2 s (INTERVALO_ENVIO_MS de trazaSoporte.js) y el contador solo sube tras guardarlo.
        await Assertions.Expect(aviso).Not.ToHaveAttributeAsync("data-interacciones-registradas", registradas!,
            new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });
        await Ayudas.CerrarSelectorTenantAsync(page);

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
        // el menú (grupo "Plataforma", CatalogoMenuLateral — resuelve la tensión
        // AdminPlataforma de #401/#403: su autoridad es de capacidad, no del
        // rol Administrador que gateaba el hub de Configuración donde vivía
        // antes), así que se entra directamente, sin pasar por el hub. Como en el
        // marco de Design (marco-talveg.js), el grupo «Plataforma» nace plegado:
        // se abre antes, como haría la persona.
        await Ayudas.AbrirGrupoYClickAsync(page, "plataforma", page.Locator("a.nav-item[href='delegaciones']"));
        // Navegación mejorada de Blazor (pushState, sin documento nuevo): el evento «load» que espera
        // WaitForURLAsync por defecto no llega, y el test caía por «Timeout 30000ms … until Load». Se espera
        // a la URL con la aserción de Playwright, que solo mira la dirección.
        await Assertions.Expect(page).ToHaveURLAsync($"{fixture.BaseUrl}/delegaciones",
            new PageAssertionsToHaveURLOptions { Timeout = 30_000 });

        await Ayudas.AbrirDetalleDeDelegacionAsync(page, Ayudas.NombreClienteDelegadoDemo, soporte: true);
        tarjeta = TarjetaSoporte(page, Ayudas.NombreClienteDelegadoDemo);
        await tarjeta.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // --- La actividad registrada incluye la concesión del acceso y la
        // navegación real, no solo el evento de apertura ---
        await tarjeta.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Ver actividad registrada", Exact = true }).ClickAsync();

        var drawer = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Actividad de soporte registrada", Exact = true });
        await drawer.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        var tabla = drawer.Locator(".tabla-datos");
        await tabla.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        var textoActividad = await tabla.InnerTextAsync();
        Assert.Contains("Acceso concedido", textoActividad);
        Assert.Contains("Navegó a", textoActividad);
        // "Pulsó" es la etiqueta de TipoActividadSoporte.Interaccion
        // (Delegaciones.razor.cs:181), leída del código y no inventada.
        Assert.Contains("Pulsó", textoActividad);

        // Cerrar el drawer para poder interactuar de nuevo con la tarjeta —
        // el botón explícito, no Escape: no depende de que dialogo-foco.js
        // ya haya movido el foco dentro del drawer.
        await drawer.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Cerrar", Exact = true }).ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });

        // --- Cerrar acceso ---
        await tarjeta.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Cerrar acceso", Exact = true }).ClickAsync();

        await tarjeta.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Abrir acceso", Exact = true }).WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        // La actividad registrada ahora incluye también el cierre.
        await tarjeta.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Ver actividad registrada", Exact = true }).ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await tabla.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        Assert.Contains("Acceso cerrado", await tabla.InnerTextAsync());
    }
}
