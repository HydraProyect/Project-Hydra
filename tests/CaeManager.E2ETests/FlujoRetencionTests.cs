using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// P1-19 de docs/business/MATURITY_REVIEW.md: retención de datos (Fase 60)
/// no tenía ningún E2E — solo CalculadoraRetencionDocumentoTests (Domain).
/// Cubre el ciclo completo de /retencion descrito en CLAUDE.md: "detectar →
/// avisar → autorizar con fecha → ejecutar", más la vía alternativa de
/// "descartar" — contra la app real, con RetencionDatos:Activa forzado a
/// true (WebAppFixtureConRetencionActiva; apagado en cualquier otro sitio,
/// ver CLAUDE.md).
///
/// Los datos purgables no se crean en el test: DatosPruebaSeeder siembra 6
/// "veteranos" (Pedro Picapiedra y compañía) específicamente para este
/// flujo — dados de baja hace ~6 años y con documentos vencidos hace ~6
/// años, bien pasado el plazo de retención por defecto (5 años). El
/// resultado de "Buscar" son siempre exactamente dos propuestas,
/// "Documentos" (18 registros = 3 documentos × 6 veteranos, incondicional)
/// y "Trabajadores dados de baja" — este segundo número SÍ varía entre
/// ejecuciones deterministas con semilla distinta: la Asignacion de cada
/// veterano solo se crea si la Empresa aleatoria que le tocó tiene al menos
/// un Centro (DatosPruebaSeeder.cs, bloque "Veteranos para la purga"), así
/// que el test no asume una cifra fija ahí — solo que la fila existe.
/// </summary>
[Collection("AppCollectionRetencion")]
public class FlujoRetencionTests(WebAppFixtureConRetencionActiva fixture)
{
    [Fact]
    public async Task Detectar_avisar_autorizar_y_ejecutar_una_purga_y_descartar_la_otra()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        // Quien administra la retención de un tenant es un Administrador DE
        // ESE tenant, y aquí lo es sin cambiar de workspace: los "veteranos"
        // viven en el Cliente Delegante de demo, y DatosPruebaSeeder siembra
        // ahí mismo un juego completo de usuarios por rol.
        //
        // Antes esto se hacía como el Administrador de ArcoSPA cambiando al
        // workspace del cliente, con un comentario que afirmaba que "el rol
        // Administrador es global a la cuenta, no por tenant". Era falso, y
        // era el agujero: la cartera de ese usuario sobre este cliente le da
        // GestorCae (DelegacionDemoSeeder.RolOperadorDelegadoDemo), no
        // Administrador. AutorizacionEscrituraBehavior ya comprobaba el rol
        // EFECTIVO para las escrituras; lo que no lo comprobaba eran las
        // puertas [Authorize(Roles = ...)] de las páginas, y por eso aquel
        // usuario entraba en /retencion con una autoridad que nadie le había
        // dado sobre este tenant. Lo cierra RolEfectivoDelWorkspaceMiddleware,
        // y el segundo test de esta clase lo fija como regresión.
        //
        // Que un operador de la consultora pueda administrar al cliente sigue
        // siendo posible — decisión del propietario del 2026-08-30 — pero la
        // autoridad tiene que venir de una Asignación de Cartera con rol
        // Administrador, nunca del rol que tenga en su propio tenant.
        await Ayudas.IniciarSesionAsync(
            page, fixture.BaseUrl, Ayudas.EmailPrueba("administrador", 1), Ayudas.ContrasenaUsuariosPrueba);

        // Los usuarios prueba.<rol> arrancan con una notificación sin leer que
        // bloquea toda interacción hasta descartarla (ver Ayudas).
        await Ayudas.DescartarNotificacionesPendientesAsync(page);

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/retencion");

        // --- Detectar ---
        await page.GetByText("Buscar datos que hayan cumplido plazo").ClickAsync();

        var tabla = page.Locator(".tabla-datos");
        await tabla.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        var filaDocumentos = tabla.Locator("tr", new LocatorLocatorOptions { HasText = "Documentos" });
        var filaTrabajadores = tabla.Locator("tr", new LocatorLocatorOptions { HasText = "Trabajadores dados de baja" });
        await filaDocumentos.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await filaTrabajadores.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        Assert.Contains("18", await filaDocumentos.InnerTextAsync());
        Assert.Contains("Pendiente de revisar", await filaDocumentos.InnerTextAsync());
        Assert.Contains("Pendiente de revisar", await filaTrabajadores.InnerTextAsync());

        // --- Avisar (solo la fila de Documentos sigue el camino completo) ---
        await filaDocumentos.GetByText("He avisado a la organización").ClickAsync();
        await AsegurarTextoEnFilaAsync(filaDocumentos, "Organización avisada");

        // --- Autorizar, con fecha de ejecución de hoy mismo para poder
        // ejecutar en el mismo test sin esperar 30 días ---
        await filaDocumentos.GetByText("Autorizar").ClickAsync();

        var modalAutorizar = page.Locator(".modal-contenido").Filter(new LocatorFilterOptions { HasText = "Autorizar la destrucción" });
        await modalAutorizar.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await modalAutorizar.GetByLabel("Fecha de ejecución").FillAsync(DateTime.UtcNow.ToString("yyyy-MM-dd"));
        await modalAutorizar.Locator(".modal-pie").GetByText("Autorizar").ClickAsync();
        await modalAutorizar.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });

        await AsegurarTextoEnFilaAsync(filaDocumentos, "Lista para ejecutar");

        // --- Ejecutar ---
        await filaDocumentos.GetByText("Ejecutar ahora").ClickAsync();

        var modalEjecutar = page.Locator(".modal-contenido").Filter(new LocatorFilterOptions { HasText = "Ejecutar la destrucción" });
        await modalEjecutar.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await modalEjecutar.Locator(".modal-pie").GetByText("Destruir definitivamente").ClickAsync();
        await modalEjecutar.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });

        await AsegurarTextoEnFilaAsync(filaDocumentos, "Ejecutada el");

        // --- La otra propuesta se descarta en vez de ejecutarse — camino
        // alternativo, no todo lo detectado termina en destrucción ---
        await filaTrabajadores.GetByText("Descartar").ClickAsync();

        var modalDescartar = page.Locator(".modal-contenido").Filter(new LocatorFilterOptions { HasText = "Descartar la propuesta" });
        await modalDescartar.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await modalDescartar.GetByLabel("Motivo").FillAsync("E2E: la organización conserva estos registros por política interna (P1-19)");
        await modalDescartar.Locator(".modal-pie").GetByText("Descartar").ClickAsync();
        await modalDescartar.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });

        await AsegurarTextoEnFilaAsync(filaTrabajadores, "Descartada");
    }

    /// <summary>
    /// La regresión de la escalada de privilegios entre tenants, extremo a
    /// extremo y con navegador real.
    ///
    /// <para>
    /// El Administrador de ArcoSPA opera el workspace del Cliente Delegante,
    /// donde su cartera le concede <b>GestorCae</b>. Antes conservaba en el
    /// <c>ClaimsPrincipal</c> el rol de su propio tenant, así que las 30
    /// puertas <c>[Authorize(Roles = …)]</c> le contestaban que sí: entraba en
    /// Retención, Configuración, Roles, Claves de API, Auditoría e
    /// Integraciones de un cliente sobre el que no tenía esa autoridad.
    /// </para>
    ///
    /// <para>
    /// Se comprueba sobre <c>/retencion</c> porque es la puerta que este mismo
    /// fichero ya sabe abrir cuando el rol SÍ corresponde: si algún día el
    /// contenido de la página cambiara, el otro test de esta clase fallaría
    /// primero y no quedaría un verde por mirar a un sitio vacío.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Un_operador_delegado_no_administra_la_retencion_del_cliente_que_opera()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(
            page, fixture.BaseUrl, Ayudas.EmailAdministradorConsultora, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, Ayudas.NombreClienteDelegadoDemo);

        await page.GotoAsync($"{fixture.BaseUrl}/retencion");

        // Se afirma la señal POSITIVA de la denegación —el destino al que
        // Routes.razor manda a un usuario autenticado sin el rol requerido— y
        // no solo la ausencia del botón. Un test que únicamente comprueba que
        // algo no se ve pasa igual de verde cuando la página no ha renderizado
        // todavía, que es exactamente el modo de fallo que no distinguiría un
        // control efectivo de una carrera ganada por accidente.
        await Assertions.Expect(page).ToHaveURLAsync(
            new System.Text.RegularExpressions.Regex("/acceso-denegado"),
            new PageAssertionsToHaveURLOptions { Timeout = 15_000 });

        // Y encima de esa ancla, lo que no puede aparecer: el control que
        // dispara la purga.
        await Assertions.Expect(page.GetByText("Buscar datos que hayan cumplido plazo"))
            .Not.ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
    }

    /// <summary>
    /// El QuickGrid/tabla se reconstruye tras cada Command (nueva consulta a
    /// ObtenerSolicitudesPurgaQuery) — esperar el texto en vez de leer una
    /// sola vez evita una carrera con el re-render.
    /// </summary>
    private static async Task AsegurarTextoEnFilaAsync(ILocator fila, string textoEsperado) =>
        await fila.GetByText(textoEsperado).WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
}
