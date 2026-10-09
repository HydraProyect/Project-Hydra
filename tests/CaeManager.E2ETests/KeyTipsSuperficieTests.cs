using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Ejecuta <c>keytips.js</c> de producción en Chromium con eventos reales de teclado y ratón,
/// junto a <c>atajos-lista.js</c> y <c>atajos-globales.js</c>: aísla la frontera DOM del modo
/// KeyTips (encender con Alt sola, niveles, salida) y su convivencia con los atajos de una
/// tecla. No sustituye el E2E sobre un listado real (<see cref="KeyTipsEnListadoTests"/>), que
/// es donde el menú lo abre Blazor.
/// </summary>
/// <remarks>
/// Lo que Chromium de Playwright no puede medir: que Alt suelta enfoque el menú del navegador
/// en Windows. Eso es de la ventana del navegador, no de la página, y se midió a mano con
/// teclas del sistema sobre Chrome y Edge (Project-Hydra-Negocio/tecnico/CAPA-USUARIO-AVANZADO-TALVEG.md § 3.3).
/// </remarks>
public class KeyTipsSuperficieTests : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private IPage _page = null!;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
        _page = await _browser.NewPageAsync();
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;
        var raiz = directorio?.FullName ?? throw new InvalidOperationException("No se encontró el árbol fuente de TALVEG.");
        await _page.RouteAsync("http://talveg.test/**", async ruta =>
        {
            var nombre = Path.GetFileName(new Uri(ruta.Request.Url).AbsolutePath);
            if (nombre.EndsWith(".js", StringComparison.Ordinal))
            {
                await ruta.FulfillAsync(new()
                {
                    ContentType = "text/javascript",
                    Body = await File.ReadAllTextAsync(Path.Combine(raiz, "src", "CaeManager.Web", "wwwroot", "js", nombre))
                });
                return;
            }
            // El menú imita a MenuAcciones: el disparador anuncia aria-expanded y el panel llega
            // DESPUÉS del clic (aquí 80 ms; en producción, una vuelta del circuito de Blazor).
            await ruta.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = """
                    <!doctype html><html lang="es"><body>
                    <button id="inicio">Inicio</button>
                    <button id="seleccion" data-keytip="S" aria-label="Selección múltiple" onclick="registrar('S')">☑</button>
                    <span data-keytip-contenedor="N"><button id="nuevo" onclick="registrar('N')">+ Nuevo centro</button></span>
                    <input id="filtro" data-keytip="F" data-filtro-pantalla aria-label="Filtrar esta pantalla">
                    <button id="pastilla" data-keytip="" data-keytip-nombre="Estado" aria-haspopup="menu" aria-expanded="false" aria-controls="panel"
                            onclick="alternarMenu()">Estado: Con vencidos</button>
                    <button id="pastilla-s" data-keytip="" onclick="registrar('subcontrata')">Subcontrata</button>
                    <span id="agrupar" role="group" aria-label="Agrupar" data-keytip="A" data-keytip-grupo>
                        <button id="sin-agrupar" aria-pressed="true" onclick="registrar('sin')">Sin agrupar</button>
                        <button id="por-cliente" aria-pressed="false" onclick="registrar('cliente')">Por Cliente</button>
                    </span>
                    <button id="oculto" data-keytip="X" style="display:none" onclick="registrar('X')">Exportar</button>
                    <select id="tamano" data-keytip="" aria-label="Mostrar"><option>20</option><option>50</option></select>
                    <button id="fecha" onclick="registrar(event.altKey ? 'emision' : 'vencimiento')">12/03/2027</button>
                    <div id="fila" class="fila-enfocada" tabindex="-1">Centro de prueba</div>
                    <script>
                        window.pulsados = [];
                        window.aperturas = 0;
                        function registrar(que) { window.pulsados.push(que); }
                        function alternarMenu() {
                            const disparador = document.getElementById('pastilla');
                            const abierto = disparador.getAttribute('aria-expanded') === 'true';
                            setTimeout(() => {
                                document.getElementById('panel')?.remove();
                                disparador.setAttribute('aria-expanded', abierto ? 'false' : 'true');
                                if (abierto) return;
                                window.aperturas++;
                                const panel = document.createElement('div');
                                panel.id = 'panel';
                                panel.setAttribute('role', 'menu');
                                panel.innerHTML = '<button role="menuitemradio" onclick="registrar(\'todos\'); alternarMenu()">Todos</button>' +
                                    '<button role="menuitemradio" onclick="registrar(\'vencidos\'); alternarMenu()">Con vencidos</button>' +
                                    '<button role="menuitemradio" disabled onclick="registrar(\'deshabilitado\')">Bloqueados</button>';
                                document.body.appendChild(panel);
                            }, 80);
                        }
                    </script>
                    </body></html>
                    """
            });
        });
        await _page.GotoAsync("http://talveg.test/");
        await _page.EvaluateAsync("""
            async () => {
                window.llamadas = [];
                const ref = { invokeMethodAsync: (metodo, tecla) => {
                    llamadas.push([metodo, tecla ?? '']);
                    return Promise.resolve();
                }};
                const lista = await import('/atajos-lista.js');
                const globales = await import('/atajos-globales.js');
                const keytips = await import('/keytips.js');
                window.keytips = keytips;
                window.suscripciones = [lista.registrarAtajosLista(ref), globales.registrarAtajosGlobales(ref),
                    keytips.registrarKeyTips({ raiz: 'raíz', nivel: 'nivel', subir: 'sube', salir: 'sale', teclaSubir: 'Atrás', teclaSalir: 'Fuera', encendido: 'encendido', apagado: 'apagado' })];
            }
            """);
        await _page.Locator("#inicio").FocusAsync();
    }

    [Fact]
    public async Task Alt_sola_enciende_con_las_letras_declaradas_y_las_deducidas_sin_tocar_las_estables()
    {
        await _page.Keyboard.PressAsync("Alt");

        Assert.Equal("raiz", await NivelAsync());
        // S, N (por el contenedor), F y A declaradas. «Estado» no puede ser E, S, T ni A (estables
        // o tomadas): le toca D. «Subcontrata» no puede ser S: le toca U. X está oculto: sin letra.
        // «Mostrar» (el desplegable) no puede ser M: le toca O.
        Assert.Equal(new SortedSet<string> { "A", "D", "F", "N", "O", "S", "U" }, await LetrasAsync());

        // Las letras son ayuda visual: la capa entera está fuera del árbol de accesibilidad, y el
        // cambio de modo se anuncia en una región de estado aparte.
        Assert.Equal("true", await _page.Locator(".keytips-capa").GetAttributeAsync("aria-hidden"));
        await Assertions.Expect(_page.Locator(".keytips-anuncio[role=status]")).ToHaveTextAsync("encendido");
    }

    [Fact]
    public async Task Una_letra_ejecuta_el_control_y_apaga()
    {
        await _page.Keyboard.PressAsync("Alt");
        await _page.Keyboard.PressAsync("n");

        Assert.Equal(["N"], await PulsadosAsync());
        Assert.Equal("apagado", await NivelAsync());
        Assert.Empty(await LetrasAsync());
        // «n» es también el atajo global «nuevo aquí»: con el modo encendido no llegó a verlo.
        Assert.Empty(await LlamadasAsync());
    }

    [Fact]
    public async Task La_letra_de_un_campo_lo_enfoca_sin_escribir_en_el()
    {
        await _page.Keyboard.PressAsync("Alt");
        await _page.Keyboard.PressAsync("f");

        await Assertions.Expect(_page.Locator("#filtro")).ToBeFocusedAsync();
        await Assertions.Expect(_page.Locator("#filtro")).ToHaveValueAsync(string.Empty);
        Assert.Equal("apagado", await NivelAsync());
    }

    [Fact]
    public async Task La_letra_de_un_grupo_baja_un_nivel_Retroceso_sube_y_Escape_sale()
    {
        await _page.Keyboard.PressAsync("Alt");
        await _page.Keyboard.PressAsync("a");

        Assert.Equal("grupo", await NivelAsync());
        Assert.Equal(new SortedSet<string> { "P", "S" }, await LetrasAsync());

        // Dentro de un grupo, elegir no saca del modo: se puede encadenar otra opción.
        await _page.Keyboard.PressAsync("p");
        Assert.Equal(["cliente"], await PulsadosAsync());
        Assert.Equal("grupo", await NivelAsync());

        await _page.Keyboard.PressAsync("Backspace");
        Assert.Equal("raiz", await NivelAsync());
        Assert.Contains("N", await LetrasAsync());

        await _page.Keyboard.PressAsync("Escape");
        Assert.Equal("apagado", await NivelAsync());
        Assert.Empty(await LetrasAsync());
    }

    [Fact]
    public async Task La_letra_de_un_menu_espera_a_que_llegue_el_panel_y_etiqueta_solo_las_opciones_habilitadas()
    {
        await _page.Keyboard.PressAsync("Alt");
        await _page.Keyboard.PressAsync("d");

        // El panel llega 80 ms después del clic: el modo no se apaga mientras tanto.
        await Assertions.Expect(_page.Locator("html")).ToHaveAttributeAsync("data-keytips", "menu");
        await Assertions.Expect(_page.Locator(".keytip")).ToHaveCountAsync(2);
        Assert.Equal(new SortedSet<string> { "C", "T" }, await LetrasAsync());

        await _page.Keyboard.PressAsync("c");
        Assert.Equal(["vencidos"], await PulsadosAsync());
        Assert.Equal("apagado", await NivelAsync());
    }

    [Fact]
    public async Task Retroceso_en_un_menu_lo_cierra_y_vuelve_a_la_raiz()
    {
        await _page.Keyboard.PressAsync("Alt");
        await _page.Keyboard.PressAsync("d");
        await Assertions.Expect(_page.Locator(".keytip")).ToHaveCountAsync(2);

        await _page.Keyboard.PressAsync("Backspace");

        await Assertions.Expect(_page.Locator("#pastilla")).ToHaveAttributeAsync("aria-expanded", "false");
        Assert.Equal("raiz", await NivelAsync());
        Assert.Empty(await PulsadosAsync());
    }

    [Fact]
    public async Task Con_el_modo_encendido_los_atajos_de_una_tecla_no_ven_la_tecla_y_al_apagarlo_vuelven()
    {
        await _page.Keyboard.PressAsync("Alt");
        // Ninguna de estas es una letra asignada en la raíz: no ejecutan nada, y tampoco llegan
        // a j/k/x (lista), al prefijo g ni a «?» (globales).
        foreach (var tecla in new[] { "j", "k", "x", "g", "c", "?" })
            await _page.Keyboard.PressAsync(tecla);

        Assert.Empty(await LlamadasAsync());
        Assert.Empty(await PulsadosAsync());
        Assert.Equal("raiz", await NivelAsync());

        // Control positivo: sin él, «no pasó nada» podría significar que las teclas no llegaban.
        await _page.Keyboard.PressAsync("Escape");
        await _page.Keyboard.PressAsync("j");
        Assert.Equal(["RecibirAtajo:j"], await LlamadasAsync());
    }

    [Fact]
    public async Task Alt_mas_clic_no_enciende_y_el_clic_conserva_su_modificador()
    {
        await _page.Keyboard.DownAsync("Alt");
        await _page.Locator("#fecha").ClickAsync();
        await _page.Keyboard.UpAsync("Alt");

        Assert.Equal(["emision"], await PulsadosAsync());
        Assert.Equal("apagado", await NivelAsync());
    }

    [Theory]
    [InlineData("Alt+j")]
    [InlineData("Control+Alt")]
    [InlineData("Shift+Alt")]
    public async Task Alt_con_otra_tecla_en_medio_no_enciende(string combinacion)
    {
        await _page.Keyboard.PressAsync(combinacion);

        Assert.Equal("apagado", await NivelAsync());
    }

    [Fact]
    public async Task No_se_enciende_dentro_de_un_campo_de_texto_ni_cancela_su_Alt()
    {
        await _page.Locator("#filtro").FocusAsync();
        await _page.EvaluateAsync("window.altCancelada = null; document.addEventListener('keydown', e => { if (e.key === 'Alt') window.altCancelada = e.defaultPrevented; })");

        await _page.Keyboard.PressAsync("Alt");

        Assert.Equal("apagado", await NivelAsync());
        // Alt + teclado numérico escribe caracteres: dentro de un campo la tecla es del sistema.
        Assert.False(await _page.EvaluateAsync<bool>("window.altCancelada"));
    }

    [Fact]
    public async Task No_se_enciende_con_un_dialogo_modal_abierto_salvo_que_lo_declare()
    {
        await _page.EvaluateAsync("""
            document.body.insertAdjacentHTML('beforeend',
                '<div id="dialogo" role="dialog" aria-modal="true"><button id="guardar" data-keytip="" onclick="registrar(\'guardar\')">Guardar</button></div>')
            """);
        await _page.Locator("#guardar").FocusAsync();

        await _page.Keyboard.PressAsync("Alt");
        Assert.Equal("apagado", await NivelAsync());

        // Declarado: solo se etiquetan los controles del diálogo, no los de la página de debajo.
        await _page.EvaluateAsync("document.getElementById('dialogo').setAttribute('data-keytips', '')");
        await _page.Keyboard.PressAsync("Alt");
        Assert.Equal("raiz", await NivelAsync());
        // «Guardar» deduce su letra: G es estable (filtros guardados), así que le toca U.
        Assert.Equal(new SortedSet<string> { "U" }, await LetrasAsync());
    }

    [Fact]
    public async Task Un_clic_o_perder_el_foco_la_ventana_apagan()
    {
        await _page.Keyboard.PressAsync("Alt");
        await _page.Locator("#fila").ClickAsync();
        Assert.Equal("apagado", await NivelAsync());

        await _page.Keyboard.PressAsync("Alt");
        Assert.Equal("raiz", await NivelAsync());
        await _page.EvaluateAsync("window.dispatchEvent(new Event('blur'))");
        Assert.Equal("apagado", await NivelAsync());

        // Alt pulsada, la ventana pierde el foco (Alt+Tab) y al volver llega solo la soltada:
        // esa Alt no estaba «limpia» y no enciende.
        await _page.Keyboard.DownAsync("Alt");
        await _page.EvaluateAsync("window.dispatchEvent(new Event('blur'))");
        await _page.Keyboard.UpAsync("Alt");
        Assert.Equal("apagado", await NivelAsync());
    }

    [Fact]
    public async Task Otra_Alt_apaga_y_una_combinacion_con_el_modo_encendido_tambien()
    {
        await _page.Keyboard.PressAsync("Alt");
        await _page.Keyboard.PressAsync("Alt");
        Assert.Equal("apagado", await NivelAsync());

        await _page.Keyboard.PressAsync("Alt");
        await _page.Keyboard.PressAsync("Control+k");
        Assert.Equal("apagado", await NivelAsync());
    }

    [Theory]
    [InlineData("Escape", "apagado")]
    [InlineData("Backspace", "raiz")]
    public async Task Salir_o_subir_antes_de_que_llegue_el_panel_lo_cierra_cuando_llega(string tecla, string nivelEsperado)
    {
        await _page.Keyboard.PressAsync("Alt");
        // Las dos teclas van seguidas: el panel tarda 80 ms y todavía no ha anunciado nada.
        await _page.Keyboard.PressAsync("d");
        await _page.Keyboard.PressAsync(tecla);
        Assert.Equal(nivelEsperado, await NivelAsync());

        // Control positivo: el panel llega a abrirse (si no, «cerrado» sería el estado de partida)…
        await _page.WaitForFunctionAsync("window.aperturas >= 1");
        // …y el módulo lo cierra en cuanto lo ve, en vez de dejarlo abierto sin modo.
        await Assertions.Expect(_page.Locator("#pastilla")).ToHaveAttributeAsync("aria-expanded", "false");
        await Assertions.Expect(_page.Locator("#panel")).ToHaveCountAsync(0);
        Assert.Equal(nivelEsperado, await NivelAsync());
        Assert.Empty(await PulsadosAsync());
    }

    [Fact]
    public async Task Con_el_foco_en_un_desplegable_Alt_enciende_y_su_letra_le_da_el_foco()
    {
        await _page.Locator("#tamano").FocusAsync();
        await _page.Keyboard.PressAsync("Alt");
        Assert.Equal("raiz", await NivelAsync());

        // «Mostrar» no puede ser M (estable): le toca O.
        await _page.Keyboard.PressAsync("o");
        await Assertions.Expect(_page.Locator("#tamano")).ToBeFocusedAsync();
        Assert.Equal("apagado", await NivelAsync());
    }

    [Fact]
    public async Task La_region_de_estado_existe_vacia_desde_el_registro_y_no_anuncia_un_apagado_sin_encendido()
    {
        var anuncio = _page.Locator(".keytips-anuncio[role=status]");
        await Assertions.Expect(anuncio).ToHaveCountAsync(1);
        await Assertions.Expect(anuncio).ToHaveTextAsync(string.Empty);

        // Sin nada que etiquetar, Alt enciende y apaga en el acto: no hay nada que anunciar.
        await _page.EvaluateAsync("document.querySelectorAll('[data-keytip], [data-keytip-contenedor]').forEach(e => e.remove())");
        await _page.Keyboard.PressAsync("Alt");
        Assert.Equal("apagado", await NivelAsync());
        await Assertions.Expect(anuncio).ToHaveTextAsync(string.Empty);
    }

    [Fact]
    public async Task La_barra_usa_los_nombres_de_tecla_que_recibe_ya_localizados()
    {
        await _page.Keyboard.PressAsync("Alt");

        Assert.Equal(["Atrás", "Fuera"], await _page.EvaluateAsync<string[]>("Array.from(document.querySelectorAll('.keytips-barra kbd')).map(k => k.textContent)"));
    }

    [Fact]
    public async Task El_dispose_de_un_registro_viejo_no_desmonta_el_registro_nuevo()
    {
        // El islote se recrea: el registro nuevo llega antes que el Dispose del viejo.
        await _page.EvaluateAsync("window.nueva = window.keytips.registrarKeyTips({}); window.suscripciones[2].dispose()");

        await _page.Keyboard.PressAsync("Alt");
        Assert.Equal("raiz", await NivelAsync());

        // Control positivo: el dispose del registro vigente sí desmonta.
        await _page.Keyboard.PressAsync("Escape");
        await _page.EvaluateAsync("window.nueva.dispose()");
        await _page.Keyboard.PressAsync("Alt");
        Assert.Equal("apagado", await NivelAsync());
    }

    [Fact]
    public async Task La_entrada_sin_teclado_enciende_el_mismo_modo()
    {
        await _page.EvaluateAsync("window.keytips.encenderKeyTips()");

        Assert.Equal("raiz", await NivelAsync());
        await _page.Keyboard.PressAsync("s");
        Assert.Equal(["S"], await PulsadosAsync());
    }

    private async Task<string> NivelAsync() =>
        await _page.EvaluateAsync<string>("document.documentElement.dataset.keytips ?? 'apagado'");

    private async Task<SortedSet<string>> LetrasAsync() =>
        new(await _page.EvaluateAsync<string[]>("Array.from(document.querySelectorAll('.keytip')).map(e => e.textContent)"));

    private async Task<string[]> PulsadosAsync() => await _page.EvaluateAsync<string[]>("window.pulsados");

    private async Task<string[]> LlamadasAsync() =>
        await _page.EvaluateAsync<string[]>("window.llamadas.map(l => l[0] + ':' + l[1])");

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }
}
