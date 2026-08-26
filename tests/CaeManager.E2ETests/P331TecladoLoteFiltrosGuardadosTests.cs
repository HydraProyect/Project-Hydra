using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Cubre P3-31 (Horizonte 1.5 de MACRO_PLAN_2026-08-13.md): atajos de
/// teclado j/k/x/Enter, selección/borrado en lote, y filtros guardados —
/// las tres piezas ya implementadas en /clientes (ver Clientes.razor,
/// AtajosListaTeclado.razor, BarraHerramientasLista.razor,
/// BarraAccionesLote.razor) pero sin ningún E2E hasta ahora. Un único test
/// encadenado, mismo criterio que FlujoCriticoTests: las tres piezas
/// comparten los mismos dos Clientes de prueba y probarlas por separado
/// solo duplicaría la creación de datos sin ganar aislamiento real.
/// </summary>
[Collection("AppCollection")]
public class P331TecladoLoteFiltrosGuardadosTests(WebAppFixture fixture)
{
    // F3b/D2 (2026-08-26): este test crea Clientes de prueba y verifica j/k,
    // "x" + BarraAccionesLote, y filtros guardados sobre /clientes —
    // respaldado por ObtenerClientesQuery, una de las 6 consultas que D2 deja
    // leyendo la tabla legacy Clientes hasta F4. Con los escritores de
    // Cliente redirigidos a Empresa, ninguno de los dos Clientes de este test
    // aparecerá jamás en esa pantalla (decisión explícita: "aceptar el
    // vacío", ver f3b-decision-d2-transicion-acotada-2026-08-25.md), así que
    // el test entero se queda sin datos que enfocar/seleccionar/filtrar.
    //
    // Se retira en vez de reancriarlo a /empresas: a diferencia de
    // AltaGuiadaTests/ImportarCombinadoTests (una comprobación puntual de
    // existencia), este test depende de la estructura completa de la lista
    // de Clientes — <tr> de QuickGrid, no las tarjetas-acordeón de
    // Empresas.razor — y de "Guardar filtro"/filtros guardados, que hoy solo
    // existe implementado en Clientes/Documentos/Trabajadores (no en
    // Empresas). Portarlo exigiría reescribir selectores contra una
    // estructura DOM distinta y los timings ya ajustados a los recursos
    // específicos de Clientes.razor.cs documentados abajo (blur/foco) — un
    // rediseño de test, no una adaptación de la aserción a la pantalla donde
    // el dato sigue existiendo.
    //
    // Cobertura real que queda intacta pese al skip: el mecanismo de atajos
    // j/k de AtajosListaTeclado ya se prueba de forma independiente en
    // FlujoBandejaPriorizadaTests (sobre /bandeja). Cobertura real que SÍ se
    // pierde con este skip, sin sustituto E2E hoy: alternar selección con
    // "x" sin activar antes "Selección múltiple", BarraAccionesLote
    // (selección en lote + borrado), y el ciclo completo de filtros
    // guardados (guardar/aplicar/borrar). Hueco reconocido, no oculto —
    // pendiente de reconstruirse cuando F4 resuelva ObtenerClientesQuery (o,
    // antes, si algún incremento lleva "Guardar filtro" a Empresas).
    [Fact(Skip = "F3b/D2: ObtenerClientesQuery congelada, /clientes vacío hasta F4 — ver f3b-decision-d2-transicion-acotada-2026-08-25.md. Ver comentario de la clase para lo que queda sin cobertura E2E.")]
    public async Task Atajos_de_teclado_seleccion_en_lote_y_filtros_guardados_funcionan_en_Clientes()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var razonSocialA = $"P331 Alfa {sufijo}";
        var razonSocialB = $"P331 Beta {sufijo}";

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

        var drawer = page.Locator(".drawer-panel");

        // --- Preparación: dos Clientes de prueba, ordenados alfabéticamente
        // (Alfa antes que Beta) para que la primera pulsación de "j" enfoque
        // siempre la misma fila, sin depender del orden de inserción. ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");

        foreach (var razonSocial in new[] { razonSocialA, razonSocialB })
        {
            await page.GetByText("+ Nuevo cliente").First.ClickAsync();
            await drawer.GetByLabel("Razón social").FillAsync(razonSocial);
            await drawer.GetByLabel("CIF", new LocatorGetByLabelOptions { Exact = true })
                .FillAsync(Ayudas.GenerarCifValido(razonSocial == razonSocialA ? 9_998_801 : 9_998_802));
            await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
            await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
        }

        // Acota el grid a los dos Clientes de este test — el tenant del
        // Administrador arranca sin datos propios (ADR-004 § 5.1), pero
        // acotar por el sufijo compartido hace el test robusto aunque se
        // reintente sin limpiar el estado anterior.
        await page.GetByPlaceholder("Buscar por nombre…").FillAsync("P331 ");
        var filaA = page.Locator("tr", new PageLocatorOptions { HasText = razonSocialA });
        var filaB = page.Locator("tr", new PageLocatorOptions { HasText = razonSocialB });
        await filaA.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await filaB.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // --- Atajos de teclado: j/k mueven el foco, x alterna selección ---
        // Tab, no clic en el h1: un encabezado no es focable (sin
        // tabindex), así que un clic ahí no mueve el foco de verdad y el
        // campo de búsqueda se lo queda — atajos-lista.js ignora
        // j/k/x/Enter mientras el foco está en un <input>/<textarea>. Causa
        // raíz real confirmada en CI con un diagnóstico temporal: el Tab sí
        // movía el foco, pero a "Solo críticos" (el siguiente <input> en el
        // DOM) — un checkbox, que también es tagName INPUT, así que
        // atajos-lista.js lo trataba igual que un campo de texto y
        // descartaba la primera "j" en silencio. Corregido en
        // wwwroot/js/atajos-lista.js distinguiendo por type, no por tagName.
        await page.Keyboard.PressAsync("Tab");

        // Salir del buscador dispara ManejarBlurAsync (CampoTexto.razor),
        // que reinvoca ValorChanged aunque el valor no haya cambiado — eso
        // vuelve a llamar a BuscarAsync -> RecargarAsync, que resetea
        // _idEnfocado a null como parte de ProveerElementosAsync
        // (Clientes.razor.cs). Sin esta espera, la primera "j" puede llegar
        // mientras esa recarga por blur sigue en vuelo y su reset de
        // _idEnfocado puede pisar el enfoque que "j" acaba de fijar.
        await page.WaitForTimeoutAsync(400);

        // Espera breve tras cada tecla, además del reintento propio de las
        // aserciones: j/k/x/Enter viajan por interop JS -> SignalR -> C# ->
        // StateHasChanged -> parche de DOM, y mandar la siguiente tecla
        // justo cuando la aserción anterior confirma el primer cambio deja
        // sin margen ese último tramo de asentamiento (detectado en CI: la
        // primera "j" pasaba, la segunda llegaba antes de que el circuito
        // terminase de procesar la primera).
        await page.Keyboard.PressAsync("j");
        await Expect(filaA).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("fila-enfocada"));
        await page.WaitForTimeoutAsync(300);

        await page.Keyboard.PressAsync("j");
        await Expect(filaB).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("fila-enfocada"));
        await page.WaitForTimeoutAsync(300);

        await page.Keyboard.PressAsync("k");
        await Expect(filaA).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("fila-enfocada"));
        await page.WaitForTimeoutAsync(300);

        // "x" alterna la selección de la fila enfocada (Alfa) sin necesidad
        // de activar antes "Selección múltiple" — AlternarSeleccion actúa
        // sobre _seleccionados directamente (ver Clientes.razor.cs).
        await page.Keyboard.PressAsync("x");
        var barraLote = page.Locator(".barra-acciones-lote");
        await barraLote.WaitForAsync(new LocatorWaitForOptions { Timeout = 5_000 });
        await Expect(barraLote.Locator(".barra-acciones-lote-cantidad")).ToHaveTextAsync("1 seleccionado");
        await page.WaitForTimeoutAsync(300);

        // --- Enter abre el drawer ligero de vista previa del Cliente enfocado;
        // "Operar →" es lo que abre el Workspace panel completo (mismo
        // patrón que el clic en el nombre de fila, ver ClientePreviewDrawer). ---
        await page.Keyboard.PressAsync("Enter");
        var previewDrawer = page.Locator(".drawer-preview-cliente");
        await previewDrawer.GetByText(razonSocialA).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await previewDrawer.GetByText("Operar →").ClickAsync();
        var workspacePanel = page.Locator(".workspace-panel");
        await workspacePanel.GetByText(razonSocialA).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await page.Keyboard.PressAsync("Escape");
        // 15s: el cierre pasa por ContextWorkspace -> actualizar la URL (?ctx=)
        // -> LocationChanged -> reconciliar estado. La causa real del cuelgue
        // intermitente (visto en CI en #209/#211) era otra: OnAfterRenderAsync
        // podía invocarse dos veces para la misma apertura (Blazor puede
        // volver a renderizar mientras el primer FocusAsync seguía en vuelo,
        // vía el propio NavigateTo de ManejarCambio) y, bajo carga real, la
        // segunda llamada a FocusAsync podía caer sobre una referencia ya
        // obsoleta del <aside> — el foco del navegador nunca llegaba a él, así
        // que el Escape del test no tenía ningún @onkeydown que lo recogiera.
        // Corregido marcando la transición como consumida antes del await en
        // OnAfterRenderAsync (ver ese método).
        await workspacePanel.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Selección múltiple visible + segunda fila por checkbox, y borrado en lote ---
        await page.GetByText("Selección múltiple").ClickAsync();
        await filaB.Locator("input[type=\"checkbox\"]").CheckAsync();
        await Expect(barraLote.Locator(".barra-acciones-lote-cantidad")).ToHaveTextAsync("2 seleccionados");

        await barraLote.GetByText("Eliminar seleccionados").ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByText("Eliminar", new LocatorGetByTextOptions { Exact = true }).ClickAsync();

        await filaA.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
        await filaB.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Filtros guardados: guardar, ver el chip, aplicarlo y borrarlo ---
        var nombreFiltro = $"P331 filtro {sufijo}";
        await page.GetByPlaceholder("Buscar por nombre…").FillAsync(razonSocialA);
        await page.GetByText("Guardar filtro").ClickAsync();

        var modalGuardarFiltro = page.GetByRole(AriaRole.Dialog).Filter(new LocatorFilterOptions { HasText = "Guardar filtro actual" });
        await modalGuardarFiltro.GetByLabel("Nombre").FillAsync(nombreFiltro);
        await modalGuardarFiltro.GetByText("Guardar", new LocatorGetByTextOptions { Exact = true }).ClickAsync();
        await modalGuardarFiltro.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });

        // El chip del filtro guardado aparece de inmediato, sin recargar.
        var chipFiltro = page.Locator(".chip-filtro", new PageLocatorOptions { HasText = nombreFiltro });
        await chipFiltro.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        // Limpiar la búsqueda a mano y volver a aplicarla desde el filtro
        // guardado demuestra que el <select> reconstruye el estado, no solo
        // que el chip existe.
        await page.GetByPlaceholder("Buscar por nombre…").FillAsync(string.Empty);
        await page.WaitForTimeoutAsync(400); // debounce de CampoTexto (300ms)

        await page.Locator("select").Filter(new LocatorFilterOptions { HasText = "Filtros guardados…" })
            .SelectOptionAsync(new SelectOptionValue { Label = nombreFiltro });
        await Expect(page.GetByPlaceholder("Buscar por nombre…")).ToHaveValueAsync(razonSocialA);

        // Borrar el filtro guardado desde su propio chip.
        await chipFiltro.Locator(".chip-filtro-quitar").ClickAsync();
        await chipFiltro.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });
    }

    private static Microsoft.Playwright.ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
