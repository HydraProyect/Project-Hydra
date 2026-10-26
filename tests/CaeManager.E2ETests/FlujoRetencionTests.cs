using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// P1-19 de Project-Hydra-Negocio/MATURITY_REVIEW.md: retención de datos (Fase 60)
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

        // El ciclo lo recorre un Administrador propio del Tenant, sin cambiar
        // de Tenant activo: los "veteranos"
        // viven en el Cliente Delegante de demo, y DatosPruebaSeeder siembra
        // ahí mismo un juego completo de usuarios por rol.
        //
        // El Administrador del Operador CAE externo también administra la retención de este Tenant
        // beneficiario (regla del 2026-10-20, segundo test de esta clase), pero el ciclo completo se
        // recorre con el Administrador propio para no depender de esa vía.
        await Ayudas.IniciarSesionAsync(
            page, fixture.BaseUrl, Ayudas.EmailPrueba("administrador", 1), Ayudas.ContrasenaUsuariosPrueba);

        // Los usuarios prueba.<rol> arrancan con una notificación sin leer que
        // bloquea toda interacción hasta descartarla (ver Ayudas).

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/retencion");

        // --- Detectar ---
        await page.GetByRole(AriaRole.Button, new() { Name = "Buscar datos que hayan cumplido plazo", Exact = true }).ClickAsync();

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
        await filaDocumentos.GetByRole(AriaRole.Button, new() { Name = "He avisado a la organización", Exact = true }).ClickAsync();
        await AsegurarTextoEnFilaAsync(filaDocumentos, "Organización avisada");

        // --- Autorizar, con fecha de ejecución de hoy mismo para poder
        // ejecutar en el mismo test sin esperar 30 días ---
        await filaDocumentos.GetByRole(AriaRole.Button, new() { Name = "Autorizar", Exact = true }).ClickAsync();

        var modalAutorizar = page.Locator(".modal-contenido").Filter(new LocatorFilterOptions { HasText = "Autorizar la destrucción" });
        await modalAutorizar.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await modalAutorizar.GetByLabel("Fecha de ejecución").FillAsync(Ayudas.HoyDeNegocio().ToString("yyyy-MM-dd"));
        await modalAutorizar.Locator(".modal-pie").GetByRole(AriaRole.Button, new() { Name = "Autorizar", Exact = true }).ClickAsync();
        await modalAutorizar.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });

        await AsegurarTextoEnFilaAsync(filaDocumentos, "Lista para ejecutar");

        // --- Ejecutar ---
        await filaDocumentos.GetByRole(AriaRole.Button, new() { Name = "Ejecutar ahora", Exact = true }).ClickAsync();

        var modalEjecutar = page.Locator(".modal-contenido").Filter(new LocatorFilterOptions { HasText = "Ejecutar la destrucción" });
        await modalEjecutar.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await modalEjecutar.Locator(".modal-pie").GetByRole(AriaRole.Button, new() { Name = "Destruir definitivamente", Exact = true }).ClickAsync();
        await modalEjecutar.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });

        await AsegurarTextoEnFilaAsync(filaDocumentos, "Ejecutada el");

        // --- La otra propuesta se descarta en vez de ejecutarse — camino
        // alternativo, no todo lo detectado termina en destrucción ---
        await filaTrabajadores.GetByRole(AriaRole.Button, new() { Name = "Descartar", Exact = true }).ClickAsync();

        var modalDescartar = page.Locator(".modal-contenido").Filter(new LocatorFilterOptions { HasText = "Descartar la propuesta" });
        await modalDescartar.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await modalDescartar.GetByLabel("Motivo").FillAsync("E2E: la organización conserva estos registros por política interna (P1-19)");
        await modalDescartar.Locator(".modal-pie").GetByRole(AriaRole.Button, new() { Name = "Descartar", Exact = true }).ClickAsync();
        await modalDescartar.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });

        await AsegurarTextoEnFilaAsync(filaTrabajadores, "Descartada");
    }

    /// <summary>
    /// Regla de Chris del 2026-10-20 (opción A, PR #1488): el Administrador del Operador CAE externo
    /// entra en cada Tenant beneficiario que cubre una Asignación de Operación vigente, externa y del
    /// Tenant entero de su organización con su rol de origen y todos sus poderes, sin Encargo de
    /// administración. Las exclusiones de los encargos no plenos, Retención entre ellas, dejan de
    /// aplicarse. Deroga la regla anterior, por la que aquí entraba con el rol <b>GestorCae</b> de su
    /// delegación heredada y la puerta de <c>/retencion</c> lo mandaba a acceso denegado.
    ///
    /// <para>
    /// Lo que sigue cerrado es la frontera de la Asignación de Operación: sin ella no entra en el
    /// Tenant (<c>EncargoAdministracionE2ETests</c>). Este test no dispara ninguna purga: mide la
    /// puerta, y el ciclo completo lo recorre el primero.
    /// </para>
    /// </summary>
    [Fact]
    public async Task El_Administrador_del_Operador_CAE_externo_administra_la_retencion_del_Tenant_beneficiario_que_opera()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(
            page, fixture.BaseUrl, Ayudas.EmailAdministradorConsultora, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, Ayudas.NombreClienteDelegadoDemo);

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/retencion");

        // Evidencia positiva: la página de Retención del Tenant beneficiario, con el control que
        // abre el ciclo, y no la de acceso denegado.
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Buscar datos que hayan cumplido plazo", Exact = true }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        Assert.DoesNotContain("/acceso-denegado", page.Url);
        await Assertions.Expect(Ayudas.DisparadorSelectorTenant(page)).ToHaveAttributeAsync(
            "data-tenant-id",
            await fixture.LeerValorSqlAsync(
                """SELECT "Id"::text FROM "Tenants" WHERE "Nombre" = @nombre""", ("nombre", Ayudas.NombreClienteDelegadoDemo)));
    }

    /// <summary>
    /// El QuickGrid/tabla se reconstruye tras cada Command (nueva consulta a
    /// ObtenerSolicitudesPurgaQuery) — esperar el texto en vez de leer una
    /// sola vez evita una carrera con el re-render.
    /// </summary>
    private static async Task AsegurarTextoEnFilaAsync(ILocator fila, string textoEsperado) =>
        await fila.GetByText(textoEsperado).WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
}
