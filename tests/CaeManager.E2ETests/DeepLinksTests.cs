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
        // Ni WaitForURLAsync ni RunAndWaitForNavigationAsync sirven de guarda
        // aquí: confirmado en vivo (captura de pantalla en el momento exacto
        // en que ambos ya daban la navegación por completa) que page.Url
        // puede quedar en /trabajadores/{id} mientras el DOM todavía
        // muestra la lista de /trabajadores con el drawer de vista previa
        // abierto — la navegación mejorada de Blazor parchea el DOM de forma
        // asíncrona y ese parcheo no coincide con ningún evento de
        // navegación que Playwright pueda esperar. La única guarda fiable es
        // esperar directamente el resultado en el DOM: que quede un único
        // ".menu-acciones-disparador" en pantalla (el de la cabecera de
        // Trabajador 360, ver TrabajadorDetalle.razor) en vez de los 20 de
        // cada fila de la lista.
        await Expect(page.Locator(".menu-acciones-disparador")).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 60_000 });
        // Esa cuenta confirma que el DOM ya es el de Trabajador 360, pero NO
        // que el componente sea interactivo: el botón llega con el
        // prerenderizado estático de @rendermode InteractiveServer y un clic
        // dado en esa ventana se pierde en silencio. AbrirMenuAccionesAsync
        // espera a aria-expanded antes de dar el menú por abierto — ver su
        // documentación en Ayudas.
        var menuTrabajador = await Ayudas.AbrirMenuAccionesAsync(page.Locator(".menu-acciones-disparador"));
        await menuTrabajador.GetByText("Editar", new LocatorGetByTextOptions { Exact = true }).ClickAsync();
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

        var disparador = page.Locator(".enlace-nombre-fila").First;
        await disparador.ClickAsync();
        var panel = page.Locator(".workspace-panel");
        await panel.Locator(".workspace-titulo-entidad").WaitForAsync();
        var tituloOriginal = (await page.Locator(".workspace-titulo-entidad").TextContentAsync())!.Trim();

        // El Context Panel no bloquea el puntero, pero el teclado no puede
        // escapar de la capa. La trampa empieza en el primer control del
        // panel; Shift+Tab desde ahí debe seguir dentro del panel (el último
        // depende de las pestañas y controles que haya cargado la entidad),
        // y Tab debe volver al primero.
        // Escape libera la trampa y devuelve el foco al disparador original
        // que sigue visible en la lista (04_UX_PATTERNS.md § 11).
        var copiarEnlace = panel.Locator(".workspace-copiar-enlace");
        await Expect(copiarEnlace).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Shift+Tab");
        var focoSigueEnElPanel = await panel.EvaluateAsync<bool>("panel => panel.contains(document.activeElement)");
        Assert.True(focoSigueEnElPanel, "Shift+Tab desde el primer control del panel no debe escapar a la página de debajo.");
        await page.Keyboard.PressAsync("Tab");
        await Expect(copiarEnlace).ToBeFocusedAsync();

        var urlConCtx = page.Url;
        Assert.Contains("ctx=Centro", urlConCtx);

        var paginaFria = await contexto.NewPageAsync();
        await Ayudas.NavegarYEsperarAsync(paginaFria, urlConCtx);

        await Expect(paginaFria.Locator(".workspace-titulo-entidad")).ToHaveTextAsync(tituloOriginal);

        await page.Keyboard.PressAsync("Escape");
        await panel.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
        await Expect(disparador).ToBeFocusedAsync();
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
        var menuDocumento = await Ayudas.AbrirMenuAccionesAsync(page.Locator(".menu-acciones-disparador").First);
        await menuDocumento.GetByText("Ver", new LocatorGetByTextOptions { Exact = true }).ClickAsync();

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

        // Por el helper y no por un ClickAsync a secas: la fila es un
        // @onclick server-side sobre una página InteractiveServer, así que un
        // clic dado en la ventana del prerenderizado se pierde en silencio y
        // el fallo saldría 30 s después esperando el botón de copiar enlace,
        // sin decir que la causa fue el clic (ver Ayudas.SeleccionarFilaBandejaAsync).
        await Ayudas.SeleccionarFilaBandejaAsync(filas.First);
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
        //
        // Sin la guarda "if (await filas.CountAsync() > 1)" que envolvía este
        // bloque: cuántas filas hay dependía del tenant compartido de
        // "AppCollection", así que el bloque podía saltarse entero y el test
        // daba verde sin haber comprobado nunca la propiedad que anuncia su
        // propio nombre. Aquí la premisa se afirma en vez de esquivarse:
        // ComunicacionesDatosPruebaSeeder siembra 38 conversaciones, 5 de
        // ellas de triage (ClienteId null, visibles a cualquier rol de gestión
        // CAE con independencia de la cartera — ver ObtenerConversacionesQuery),
        // y ningún otro test E2E crea, asigna ni borra conversaciones. Menos
        // de dos filas es un cambio de la siembra o del alcance que hay que
        // ver fallar, no una razón para no probar nada.
        var totalFilas = await filas.CountAsync();
        Assert.True(
            totalFilas >= 2,
            $"La bandeja trajo {totalFilas} fila(s) y hacen falta dos hilos distintos para probar que cambiar " +
            "de conversación actualiza \"conversacion\" en la URL. ComunicacionesDatosPruebaSeeder siembra 38 " +
            "conversaciones (5 de triage, visibles con independencia de la cartera): si aquí no hay dos, lo que " +
            "cambió es la siembra o el alcance de datos, y esta propiedad se quedó sin cubrir.");

        // La fila objetivo se elige por la marca que pone el propio servidor
        // ("bandeja-fila-activa" sale de _conversacionSeleccionadaId), no por
        // un índice a ciegas. Clicar la conversación YA abierta dejaría este
        // bloque esperando para siempre sin que hubiera ningún defecto de
        // producto: SeleccionarConversacionAsync no toca la URL en ese caso a
        // propósito — la guarda «if (id.ToString() != ConversacionInicial)»
        // de Bandeja.razor.cs existe para cortar el bucle de redirecciones
        // del prerenderizado.
        var indiceObjetivo = await IndiceDePrimeraFilaNoActivaAsync(filas, totalFilas);
        var filaObjetivo = filas.Nth(indiceObjetivo);
        var asuntoObjetivo = (await filaObjetivo.Locator(".bandeja-fila-asunto").TextContentAsync())!.Trim();

        await Ayudas.SeleccionarFilaBandejaAsync(filaObjetivo);

        // Solo ahora se espera la URL: el helper ya ha confirmado que el clic
        // llegó al circuito y que el servidor tiene seleccionado OTRO hilo,
        // así que si la URL no cambia el fallo es de la feature, no del clic.
        try
        {
            await page.WaitForURLAsync(url => url.Contains("conversacion=") && url != urlConversacionOriginal);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"El servidor ya marcó como activa la fila {indiceObjetivo} (otro hilo distinto del abierto), " +
                $"pero la URL se quedó en «{page.Url}» en vez de actualizar \"conversacion\". El clic sí llegó al " +
                "circuito, así que esto no es un flake de Playwright: es el deep-link de Conversación no " +
                "reflejando el hilo seleccionado (ver SeleccionarConversacionAsync en Bandeja.razor.cs).",
                ex);
        }

        // La URL cambió; que además sea el hilo que se clicó —y no cualquier
        // otro— lo comprueba el detalle del centro.
        await Expect(page.Locator(".bandeja-centro-titulo-fila h2")).ToHaveTextAsync(asuntoObjetivo);
    }

    /// <summary>
    /// Índice de la primera <c>.bandeja-fila</c> que el servidor NO tiene
    /// seleccionada. La clase <c>bandeja-fila-activa</c> la renderiza Blazor
    /// desde <c>_conversacionSeleccionadaId</c> (ver Bandeja.razor), así que
    /// es la propia app quien dice cuál es el hilo abierto — no hace falta
    /// suponer que la lista conserva el orden ni que <c>Nth(1)</c> es otro
    /// hilo.
    /// </summary>
    private static async Task<int> IndiceDePrimeraFilaNoActivaAsync(ILocator filas, int total)
    {
        for (var indice = 0; indice < total; indice++)
        {
            var clases = await filas.Nth(indice).GetAttributeAsync("class") ?? string.Empty;
            if (!clases.Contains("bandeja-fila-activa"))
                return indice;
        }

        throw new InvalidOperationException(
            $"Las {total} filas de la bandeja están marcadas como activas a la vez — imposible con un único " +
            "_conversacionSeleccionadaId (ver Bandeja.razor), así que o el marcado de la fila cambió o la " +
            "lista se está leyendo a mitad de un re-render.");
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
