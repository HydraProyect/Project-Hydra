using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Horizonte 2.6 de MACRO_PLAN_2026-08-13.md ("Deep-links y rutas de detalle") y
/// § 7 punto 3 ("Deep-links + «copiar enlace» en cada entidad"): rutas reales
/// para Trabajador, Centro, Documento y Conversación que reconstruyen el
/// panel/drawer de detalle a partir SOLO de la URL, en una carga en frío — sin
/// ningún estado de circuito de Blazor previo (justo el hueco que
/// ContextWorkspace.razor documentaba como pendiente en su comentario de
/// "ParametroCtx" antes de esta feature).
///
/// Para Trabajador/Centro/Documento el mecanismo ya es genérico
/// (ContextWorkspace.razor, query "ctx", PR #191 "deep-link del Context
/// Panel") — este test verifica que de verdad funciona desde una navegación
/// de navegador real y con página nueva (sin circuito previo), no solo que el
/// switch de EntidadWorkspace incluya esos tipos. Conversación no pasa por el
/// Context Workspace (la Bandeja unificada de /comunicaciones es
/// maestro-detalle propio, Features/Comunicaciones/Pages/Bandeja) y tiene su
/// propio parámetro de query "conversacion", con el mismo criterio "la URL
/// manda" que el resto de filtros de esa página.
///
/// Login con un usuario prueba.gestorcae (mismo patrón que AlcanceRolesTests)
/// en vez del Administrador de plataforma o el de la Consultora: la
/// Consultora en sí es un tenant SIN datos operativos propios (ADR-004 § 5.1,
/// ver DelegacionDemoSeeder) y el Administrador de plataforma (TALVEG)
/// arranca vacío a propósito — la cartera de prueba real (trabajadores,
/// centros, documentos, conversaciones) vive dentro del tenant Cliente
/// Delegante "Laboratorios Dexter", que es donde pertenecen los usuarios
/// prueba.&lt;rol&gt;.
/// </summary>
[Collection("AppCollection")]
public class DeepLinksTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Deep_link_de_Trabajador_reconstruye_el_panel_en_frio_copia_el_enlace_y_se_limpia_al_cerrar()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        await contexto.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");

        // Abre el primer trabajador desde la propia interfaz (clic, no un
        // "ctx" escrito a mano) — así la URL que se prueba después es
        // exactamente la que ContextWorkspace.ActualizarUrlDesdeEstado genera
        // de verdad, no una inventada por el test. El clic en el nombre de
        // fila abre primero el drawer ligero de vista previa (TrabajadorPreviewDrawer);
        // "Ver Trabajador 360 →" lleva a la ficha completa, y desde ahí
        // "⋯ → Editar" es lo que de verdad abre el Context Workspace — el
        // mismo camino que ya usa TrabajadorDetalle.razor.cs (AbrirInformacion).
        await page.Locator(".enlace-nombre-fila").First.ClickAsync();
        await page.GetByText("Ver Trabajador 360 →").ClickAsync();
        // Espera la navegación real a /trabajadores/{id} antes de interactuar
        // con su "⋯" — sin esto, Locator(".menu-acciones-disparador") puede
        // evaluarse contra el DOM todavía en transición desde /trabajadores
        // (20 filas, cada una con su propio "⋯"), y el modo estricto de
        // Playwright no reintenta por cardinalidad como sí hace por
        // visibilidad.
        await page.WaitForURLAsync(new System.Text.RegularExpressions.Regex(@"/trabajadores/[0-9a-fA-F-]{36}"));
        await page.Locator(".menu-acciones-disparador").ClickAsync();
        await page.Locator(".menu-acciones-panel").GetByText("Editar", new LocatorGetByTextOptions { Exact = true }).ClickAsync();
        await page.Locator(".workspace-titulo-entidad").WaitForAsync();
        var tituloOriginal = (await page.Locator(".workspace-titulo-entidad").TextContentAsync())!.Trim();

        var urlConCtx = page.Url;
        Assert.Contains("ctx=Trabajador", urlConCtx);

        // "Copiar enlace" (§ 7.3): el portapapeles debe quedar con
        // exactamente la URL actual, sin transformarla. Se espera al toast de
        // confirmación (no solo al clic) porque copiarAlPortapapeles es un
        // JSInterop asíncrono — leer el portapapeles justo tras el clic puede
        // adelantarse a que la escritura real haya terminado.
        await page.Locator(".workspace-copiar-enlace").ClickAsync();
        await page.GetByText("Se copió el enlace a esta ficha").WaitForAsync();
        var enlaceCopiado = await page.EvaluateAsync<string>("navigator.clipboard.readText()");
        Assert.Equal(urlConCtx, enlaceCopiado);

        // --- La carga en frío real: pestaña nueva del mismo contexto
        // autenticado, que nunca pasó por /trabajadores en su propio
        // circuito — nada de estado en memoria que "recordar", solo la URL. ---
        var paginaFria = await contexto.NewPageAsync();
        await Ayudas.NavegarYEsperarAsync(paginaFria, urlConCtx);

        await Expect(paginaFria.Locator(".workspace-titulo-entidad")).ToHaveTextAsync(tituloOriginal);

        // Cierre al navegar (el hueco que ContextWorkspace.razor documentaba
        // como pendiente junto al deep-link): el botón "×" quita "ctx" de la
        // URL en vez de dejarlo colgado.
        await paginaFria.Locator(".workspace-cerrar").ClickAsync();
        await paginaFria.WaitForURLAsync(url => !url.Contains("ctx="));
    }

    [Fact]
    public async Task Deep_link_de_Centro_reconstruye_el_panel_en_frio()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/centros");

        await page.Locator(".enlace-nombre-fila").First.ClickAsync();
        await page.Locator(".workspace-titulo-entidad").WaitForAsync();
        var tituloOriginal = (await page.Locator(".workspace-titulo-entidad").TextContentAsync())!.Trim();

        var urlConCtx = page.Url;
        Assert.Contains("ctx=Centro", urlConCtx);

        var paginaFria = await contexto.NewPageAsync();
        await Ayudas.NavegarYEsperarAsync(paginaFria, urlConCtx);

        await Expect(paginaFria.Locator(".workspace-titulo-entidad")).ToHaveTextAsync(tituloOriginal);
    }

    [Fact]
    public async Task Deep_link_de_Documento_reconstruye_el_panel_en_frio()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos");

        // Documentos abre el panel desde el menú "⋯" de la fila (MenuAcciones),
        // no de un enlace directo como Trabajadores/Centros.
        await page.Locator(".menu-acciones-disparador").First.ClickAsync();
        await page.Locator(".menu-acciones-panel").GetByText("Ver", new LocatorGetByTextOptions { Exact = true }).ClickAsync();

        await page.Locator(".workspace-titulo-entidad").WaitForAsync();
        var tituloOriginal = (await page.Locator(".workspace-titulo-entidad").TextContentAsync())!.Trim();

        var urlConCtx = page.Url;
        Assert.Contains("ctx=Documento", urlConCtx);

        var paginaFria = await contexto.NewPageAsync();
        await Ayudas.NavegarYEsperarAsync(paginaFria, urlConCtx);

        await Expect(paginaFria.Locator(".workspace-titulo-entidad")).ToHaveTextAsync(tituloOriginal);
    }

    /// <summary>
    /// Conversación no comparte mecanismo con las otras tres (no hay Context
    /// Workspace en /comunicaciones) — cubre a la vez la carga en frío,
    /// "copiar enlace", y que seleccionar OTRA conversación actualiza
    /// "conversacion" en la URL en vez de dejar la anterior colgada (mismo
    /// "cierre al navegar" que el resto de la feature, aplicado aquí a
    /// cambiar de selección en vez de salir de la página).
    /// </summary>
    [Fact]
    public async Task Deep_link_de_Conversacion_reconstruye_el_detalle_en_frio_copia_el_enlace_y_actualiza_la_url_al_cambiar_de_hilo()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        await contexto.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.DescartarNotificacionesPendientesAsync(page);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/comunicaciones");

        var filas = page.Locator(".bandeja-fila");
        await filas.First.WaitForAsync();

        await filas.First.ClickAsync();
        await page.Locator(".bandeja-centro-copiar-enlace").WaitForAsync();
        var asuntoOriginal = (await page.Locator(".bandeja-centro-titulo-fila h2").TextContentAsync())!.Trim();

        var urlConversacionOriginal = page.Url;
        Assert.Contains("conversacion=", urlConversacionOriginal);

        // "Copiar enlace" (§ 7.3): mismo criterio que el Context Workspace —
        // el portapapeles queda con la URL actual, sin transformarla. Se
        // espera al toast (JSInterop asíncrono, ver comentario equivalente
        // en el test de Trabajador) antes de leer el portapapeles.
        await page.Locator(".bandeja-centro-copiar-enlace").ClickAsync();
        await page.GetByText("Se copió el enlace a esta conversación").WaitForAsync();
        var enlaceCopiado = await page.EvaluateAsync<string>("navigator.clipboard.readText()");
        Assert.Equal(urlConversacionOriginal, enlaceCopiado);

        // --- Carga en frío: pestaña nueva, mismo criterio que los otros tres. ---
        var paginaFria = await contexto.NewPageAsync();
        await Ayudas.NavegarYEsperarAsync(paginaFria, urlConversacionOriginal);

        await Expect(paginaFria.Locator(".bandeja-centro-titulo-fila h2")).ToHaveTextAsync(asuntoOriginal);

        // --- Cambiar de hilo actualiza "conversacion" en la URL — no queda
        // colgado el id del hilo anterior. ---
        if (await filas.CountAsync() > 1)
        {
            await filas.Nth(1).ClickAsync();
            await page.WaitForURLAsync(url => url.Contains("conversacion=") && url != urlConversacionOriginal);
        }
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
