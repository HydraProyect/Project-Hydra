using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Cubre P3-31 (Horizonte 1.5 de Project-Hydra-Negocio/MACRO_PLAN_2026-08-13.md): atajos de
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
    // F3b/D2 (2026-08-26) retiró este test porque los dos Clientes que crea no
    // llegaban a aparecer en /clientes: ObtenerClientesQuery seguía leyendo la
    // tabla legacy Clientes, que ya no recibía escrituras, y el test se quedaba
    // sin datos que enfocar, seleccionar ni filtrar. Su propio comentario
    // dejaba escrita la condición de vuelta — "cuando F4 resuelva
    // ObtenerClientesQuery" — y F4 (2026-08-27, #288/#291) la cumplió.
    // Repuesto el 2026-08-28.
    //
    // Con él vuelve la cobertura que el skip declaraba perdida y sin
    // sustituto: alternar selección con "x" sin activar antes "Selección
    // múltiple", BarraAccionesLote (selección en lote + borrado) y el ciclo
    // completo de filtros guardados (guardar/aplicar/borrar). Los atajos j/k
    // seguían cubiertos aparte en FlujoBandejaPriorizadaTests (sobre
    // /bandeja); el resto no lo estaba en ningún sitio.
    //
    // Comprobado por mutación: forzando ObtenerClientesQuery a no devolver
    // nada — la condición exacta de F3b — este test falla esperando la fila
    // de "P331 Alfa". Depende de verdad de que la lista muestre lo que se
    // acaba de crear, no pasa por inercia.
    [Fact]
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
            await page.GetByText("+ Nuevo Cliente empresarial").First.ClickAsync();
            await drawer.GetByLabel("Razón social").FillAsync(razonSocial);
            await drawer.GetByLabel("Identificación fiscal", new LocatorGetByLabelOptions { Exact = true })
                .FillAsync(Ayudas.GenerarCifValido(razonSocial == razonSocialA ? 9_998_801 : 9_998_802));
            await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
            await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
        }

        // Acota el grid a los dos Clientes de este test — el tenant del
        // Administrador arranca sin datos propios (ADR-004 § 5.1), pero
        // acotar por el sufijo compartido hace el test robusto aunque se
        // reintente sin limpiar el estado anterior.
        await page.GetByPlaceholder("Filtrar esta pantalla: nombre").FillAsync("P331 ");
        var filaA = page.Locator("tr", new PageLocatorOptions { HasText = razonSocialA });
        var filaB = page.Locator("tr", new PageLocatorOptions { HasText = razonSocialB });
        await filaA.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await filaB.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // --- Atajos de teclado: j/k mueven el foco, x alterna selección ---
        // El primer Tab desde el buscador llega a la primera pastilla de filtro
        // («Gestor CAE»): es un botón, y los botones no consumen j/k/x, así que
        // desde ahí j/k sí deben recorrer la lista. No suponemos más que eso.
        await page.Keyboard.PressAsync("Tab");
        var pastillaGestor = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Gestor CAE", Exact = true });
        await Expect(pastillaGestor).ToBeFocusedAsync();
        await Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Criticidad", Exact = true })).ToBeVisibleAsync();

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
        // La fila "enfocada" por j/k debe llevarse también el foco real de
        // DOM, no solo la clase CSS — si no, un lector de pantalla nunca se
        // entera de cuál es (defecto detectado en revisión de Plantillas,
        // fix en atajos-lista.js: enfocarFilaActiva()).
        await Expect(filaA).ToBeFocusedAsync();
        await page.WaitForTimeoutAsync(300);

        await page.Keyboard.PressAsync("j");
        await Expect(filaB).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("fila-enfocada"));
        await Expect(filaB).ToBeFocusedAsync();
        await page.WaitForTimeoutAsync(300);

        await page.Keyboard.PressAsync("k");
        await Expect(filaA).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("fila-enfocada"));
        await Expect(filaA).ToBeFocusedAsync();
        await page.WaitForTimeoutAsync(300);

        // "x" alterna la selección de la fila enfocada (Alfa) sin necesidad
        // de activar antes "Selección múltiple" — AlternarSeleccion actúa
        // sobre _seleccionados directamente (ver Clientes.razor.cs).
        await page.Keyboard.PressAsync("x");
        var barraLote = page.Locator(".barra-acciones-lote");
        await barraLote.WaitForAsync(new LocatorWaitForOptions { Timeout = 5_000 });
        await Expect(barraLote.Locator(".barra-acciones-lote-cantidad")).ToHaveTextAsync("1 seleccionado en esta página");
        await page.WaitForTimeoutAsync(300);

        // --- Enter abre la vista previa lateral del Cliente enfocado
        // (ClientePreviewDrawer, pieza 6 del patrón de lista), la misma que
        // abre el nombre de la fila. Desde el patrón, el panel de 520 px del
        // Context Workspace solo se abre con «Ver toda su documentación» de
        // la vista previa. El drawer no maneja Escape: se cierra con su ✕. ---
        await page.Keyboard.PressAsync("Enter");
        var vistaPrevia = page.GetByRole(AriaRole.Complementary, new PageGetByRoleOptions { Name = "Vista previa del Cliente empresarial" });
        await vistaPrevia.Locator(".nombre-cabecera-preview-cliente", new LocatorLocatorOptions { HasText = razonSocialA })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await vistaPrevia.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Cerrar" }).ClickAsync();
        await vistaPrevia.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Selección múltiple visible + segunda fila por checkbox, y borrado en lote ---
        // El conmutador es el icono ☑ de la cabecera: sin texto visible, con el nombre accesible de siempre.
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Selección múltiple", Exact = true }).ClickAsync();
        await filaB.Locator("input[type=\"checkbox\"]").CheckAsync();
        await Expect(barraLote.Locator(".barra-acciones-lote-cantidad")).ToHaveTextAsync("2 seleccionados en esta página");

        await barraLote.GetByText("Dar de baja seleccionados").ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByText("Dar de baja", new LocatorGetByTextOptions { Exact = true }).ClickAsync();

        await filaA.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
        await filaB.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Filtros guardados (dentro de «Más filtros»): guardar, verlo, aplicarlo y borrarlo ---
        var nombreFiltro = $"P331 filtro {sufijo}";
        var buscador = page.GetByPlaceholder("Filtrar esta pantalla: nombre");
        var masFiltros = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Más filtros", Exact = true });
        var filtroGuardado = page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { Name = nombreFiltro, Exact = true });

        await buscador.FillAsync(razonSocialA);
        await page.WaitForTimeoutAsync(400); // debounce de CampoTexto (300ms): «Guardar filtro» se habilita con un filtro aplicado
        await masFiltros.ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { Name = "Guardar filtro", Exact = true }).ClickAsync();

        var modalGuardarFiltro = page.GetByRole(AriaRole.Dialog).Filter(new LocatorFilterOptions { HasText = "Guardar filtro actual" });
        await modalGuardarFiltro.GetByLabel("Nombre").FillAsync(nombreFiltro);
        await modalGuardarFiltro.GetByText("Guardar", new LocatorGetByTextOptions { Exact = true }).ClickAsync();
        await modalGuardarFiltro.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });

        // Limpiar la búsqueda a mano y volver a aplicarla desde el filtro
        // guardado demuestra que reconstruye el estado, no solo que existe.
        // El filtro está en «Más filtros» de inmediato, sin recargar.
        await buscador.FillAsync(string.Empty);
        await page.WaitForTimeoutAsync(400); // debounce de CampoTexto (300ms)

        await masFiltros.ClickAsync();
        await filtroGuardado.ClickAsync();
        await Expect(buscador).ToHaveValueAsync(razonSocialA);

        // Borrar el filtro guardado desde su ✕ en «Más filtros». El borrado pide
        // confirmación (no tiene deshacer): sigue ahí hasta que se confirma.
        await masFiltros.ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { Name = $"Borrar filtro guardado {nombreFiltro}", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByText("Borrar filtro", new LocatorGetByTextOptions { Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });
        await masFiltros.ClickAsync();
        await Expect(filtroGuardado).ToHaveCountAsync(0);
    }

    /// <summary>
    /// Rediseño de listados, fase 1: la tecla «f» (sin modificadores y con el foco fuera de un
    /// campo) enfoca el buscador «Filtrar esta pantalla», y la «f» no queda escrita en él. La
    /// chuleta («?») la anuncia. Ctrl/Cmd+K sigue siendo el buscador universal de la cabecera.
    /// </summary>
    [Fact]
    public async Task F_enfoca_el_buscador_de_la_pantalla_sin_escribir_la_tecla()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");

        var buscador = page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Filtrar esta pantalla", Exact = true });
        await Expect(buscador).Not.ToBeFocusedAsync();

        // Un botón con el foco no consume la «f» (mismo criterio que j/k).
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Selección múltiple", Exact = true }).FocusAsync();
        await page.Keyboard.PressAsync("f");

        await Expect(buscador).ToBeFocusedAsync();
        await Expect(buscador).ToHaveValueAsync(string.Empty);
    }

    /// <summary>
    /// Casos negativos de la «f»: escribiendo en un campo, la «f» es una letra más; con un menú
    /// abierto (una pastilla de filtro), el teclado es del menú y el foco no salta al buscador.
    /// </summary>
    [Fact]
    public async Task F_no_actua_escribiendo_en_un_campo_ni_con_un_menu_abierto()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");

        var buscador = page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Filtrar esta pantalla", Exact = true });

        // Menú abierto: la pastilla «Criticidad» enfoca su opción vigente («Todos»). Hasta el 2026-10-08 se
        // usaba la pastilla «Estado», que pasó a ser la franja de estado (botones, sin menú).
        var pastillaEstado = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Criticidad", Exact = true });
        await pastillaEstado.ClickAsync();
        var todos = page.GetByRole(AriaRole.Menuitemradio, new PageGetByRoleOptions { Name = "Todos", Exact = true });
        await Expect(todos).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("f");
        await Expect(todos).ToBeFocusedAsync();
        await Expect(buscador).Not.ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Escape");
        await Expect(pastillaEstado).ToHaveAttributeAsync("aria-expanded", "false");

        // Campo editable: la «f» se escribe (se usa el propio buscador, sin debounce que esperar
        // para leer su valor).
        await buscador.FocusAsync();
        await page.Keyboard.PressAsync("f");
        await Expect(buscador).ToHaveValueAsync("f");
    }

    /// <summary>
    /// Defecto de accesibilidad (WCAG 2.1.1) encontrado en revisión de
    /// Plantillas: atajos-lista.js interceptaba Enter con preventDefault()
    /// sin mirar dónde estaba el foco, así que tabular hasta CUALQUIER botón
    /// o enlace de una lista Gen 2 y pulsar Enter abría la fila "enfocada"
    /// por j/k en vez de activar el control con el foco — el usuario de
    /// teclado nunca podía disparar "+ Nuevo Cliente empresarial" con Enter.
    /// </summary>
    [Fact]
    public async Task Enter_sobre_un_boton_enfocado_activa_el_boton_y_no_el_atajo_de_fila()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");

        // FocusAsync en vez de Tab real, mismo motivo que
        // FlujoBandejaPriorizadaTests: a atajos-lista.js solo le importa
        // document.activeElement en el momento del keydown, no cuántos Tabs
        // hicieron falta para llegar ahí, y depender del orden de tabulación
        // real es frágil en Chromium headless.
        var botonNuevoCliente = page.GetByText("+ Nuevo Cliente empresarial").First;
        await botonNuevoCliente.FocusAsync();
        await Expect(botonNuevoCliente).ToBeFocusedAsync();

        await page.Keyboard.PressAsync("Enter");

        // Con el defecto (preventDefault incondicional sobre Enter), este
        // drawer nunca se abría: el click nativo del botón se cancelaba y
        // "j"/"k" tampoco habían fijado ninguna fila enfocada, así que
        // RecibirAtajo("Enter") no tenía nada que abrir.
        var drawer = page.Locator(".drawer-panel");
        await Expect(drawer.GetByText("Nuevo Cliente empresarial", new LocatorGetByTextOptions { Exact = true }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
        // Ni la vista previa de ninguna fila: Enter era del botón, no de la lista.
        await Expect(page.Locator(".drawer-preview-cliente")).Not.ToBeVisibleAsync();
        await Expect(page.Locator(".workspace-panel")).Not.ToBeVisibleAsync();
    }

    private static Microsoft.Playwright.ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
