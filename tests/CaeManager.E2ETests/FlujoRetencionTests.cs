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

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministradorConsultora, Ayudas.ContrasenaUsuariosPrueba);

        // Los "veteranos" viven en el Delegated Workspace de demo, no en el
        // tenant de origen del Administrador (la Consultora no tiene datos
        // operativos propios) — el rol Administrador es global a la cuenta,
        // no por tenant, así que sigue teniendo acceso a /retencion tras
        // cambiar de workspace (ver ADR-004). El Administrador que opera el
        // Delegated Workspace es el de ArcoSPA, no admin@caemanager.local
        // (ver DelegacionDemoSeeder, 2026-08-14).
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, Ayudas.NombreClienteDelegadoDemo);
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
    /// El QuickGrid/tabla se reconstruye tras cada Command (nueva consulta a
    /// ObtenerSolicitudesPurgaQuery) — esperar el texto en vez de leer una
    /// sola vez evita una carrera con el re-render.
    /// </summary>
    private static async Task AsegurarTextoEnFilaAsync(ILocator fila, string textoEsperado) =>
        await fila.GetByText(textoEsperado).WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
}
