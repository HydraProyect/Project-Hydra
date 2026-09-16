using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Ejecuta los módulos JS de producción en Chromium con eventos reales. Aísla
/// la frontera DOM/interop: no sustituye los flujos E2E con Blazor y base de datos.
/// </summary>
public class AtajosSuperficiesTests : IAsyncLifetime
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
            await ruta.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = """
                    <!doctype html><html lang="es"><body>
                    <button id="inicio">Abrir</button>
                    <div id="fila" class="fila-enfocada" tabindex="-1">Empresa de prueba</div>
                    <input id="texto" aria-label="Buscar">
                    <input id="casilla" type="checkbox" aria-label="Seleccionar empresa">
                    <select id="selector" aria-label="Estado"><option>Todos</option><option>Justificados</option></select>
                    <div id="editor" contenteditable="true" aria-label="Comentario"></div>
                    <button id="accion" onclick="this.dataset.activado = 'si'">Acción nativa</button>
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
                const buscador = await import('/buscador-global.js');
                window.suscripciones = [lista.registrarAtajosLista(ref), globales.registrarAtajosGlobales(ref), buscador.registrarAtajoBuscador(ref)];
            }
            """);
        await _page.Locator("#inicio").FocusAsync();
    }

    [Theory]
    [InlineData("Control")]
    [InlineData("Meta")]
    public async Task El_buscador_no_mueve_la_fila_con_su_modificador(string modificador)
    {
        await _page.Keyboard.PressAsync($"{modificador}+k");
        Assert.Equal(["AbrirDesdeJs:"], await LlamadasAsync());
        await _page.Keyboard.PressAsync("j");
        await Assertions.Expect(_page.Locator("#fila")).ToBeFocusedAsync();
        Assert.Equal(["AbrirDesdeJs:", "RecibirAtajo:j"], await LlamadasAsync());
    }

    [Theory]
    [InlineData("Control+x")]
    [InlineData("Meta+x")]
    [InlineData("Alt+j")]
    public async Task Las_combinaciones_no_se_convierten_en_atajos_de_lista(string tecla)
    {
        await _page.Keyboard.PressAsync(tecla);
        Assert.Empty(await LlamadasAsync());
        await _page.Keyboard.PressAsync("x");
        Assert.Equal(["RecibirAtajo:x"], await LlamadasAsync());
    }

    [Theory]
    [InlineData("dialog", false)]
    [InlineData("dialog", true)]
    [InlineData("alertdialog", false)]
    [InlineData("native", false)]
    public async Task El_dialogo_suspende_atajos_aunque_el_foco_siga_fuera(string tipo, bool focoFuera)
    {
        await AbrirDialogoAsync(tipo);
        if (focoFuera) await _page.Locator("#inicio").FocusAsync();
        foreach (var tecla in new[] { "j", "k", "x", "g", "c", "n", "?", "Control+k" })
            await _page.Keyboard.PressAsync(tecla);
        Assert.Empty(await LlamadasAsync());

        await _page.EvaluateAsync("document.querySelector('#dialogo').remove()");
        await _page.Locator("#inicio").FocusAsync();
        await _page.Keyboard.PressAsync("j");
        await Assertions.Expect(_page.Locator("#fila")).ToBeFocusedAsync();
        Assert.Equal(["RecibirAtajo:j"], await LlamadasAsync());
    }

    [Fact]
    public async Task Un_dialogo_oculto_no_bloquea_la_pagina_y_el_prefijo_no_cruza_el_dialogo()
    {
        await _page.Keyboard.PressAsync("g");
        await AbrirDialogoAsync("dialog");
        await _page.Keyboard.PressAsync("c");
        await _page.EvaluateAsync("document.querySelector('#dialogo').hidden = true");
        await _page.Locator("#inicio").FocusAsync();
        await _page.Keyboard.PressAsync("c");
        Assert.Empty(await LlamadasAsync());
        await _page.Keyboard.PressAsync("g");
        await _page.Keyboard.PressAsync("c");
        Assert.Equal(["IrA:c"], await LlamadasAsync());
    }

    [Theory]
    [InlineData("#texto")]
    [InlineData("#editor")]
    [InlineData("#selector")]
    public async Task La_edicion_y_los_selectores_conservan_sus_teclas(string selector)
    {
        await _page.Locator(selector).FocusAsync();
        foreach (var tecla in new[] { "j", "k", "x", "g", "c", "n", "?" })
            await _page.Keyboard.PressAsync(tecla);
        Assert.Empty(await LlamadasAsync());
        await _page.Keyboard.PressAsync("Control+k");
        Assert.Equal(["AbrirDesdeJs:"], await LlamadasAsync());
    }

    [Fact]
    public async Task Enter_activa_el_boton_y_la_casilla_no_bloquea_la_lista()
    {
        await _page.Locator("#accion").FocusAsync();
        await _page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(_page.Locator("#accion")).ToHaveAttributeAsync("data-activado", "si");
        Assert.Empty(await LlamadasAsync());
        await _page.Locator("#casilla").FocusAsync();
        await _page.Keyboard.PressAsync("j");
        await Assertions.Expect(_page.Locator("#fila")).ToBeFocusedAsync();
        Assert.Equal(["RecibirAtajo:j"], await LlamadasAsync());
    }

    [Fact]
    public async Task Una_respuesta_de_lista_tardia_no_roba_el_foco_del_dialogo()
    {
        await _page.EvaluateAsync("""
            async () => {
                suscripciones[0].dispose();
                const lista = await import('/atajos-lista.js');
                suscripciones[0] = lista.registrarAtajosLista({ invokeMethodAsync: () =>
                    new Promise(resolve => window.resolverLista = resolve) });
            }
            """);
        await _page.Keyboard.PressAsync("j");
        await _page.WaitForFunctionAsync("typeof window.resolverLista === 'function'");
        await AbrirDialogoAsync("dialog");
        await _page.EvaluateAsync("async () => { resolverLista(); await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))); }");
        await Assertions.Expect(_page.Locator("#cerrar")).ToBeFocusedAsync();
    }

    private Task AbrirDialogoAsync(string tipo) => _page.EvaluateAsync("""
        tipo => {
            const dialogo = document.createElement(tipo === 'native' ? 'dialog' : 'div');
            dialogo.id = 'dialogo';
            if (tipo !== 'native') {
                dialogo.setAttribute('role', tipo);
                dialogo.setAttribute('aria-modal', 'true');
            }
            dialogo.innerHTML = '<button id="cerrar">Cerrar</button>';
            document.body.append(dialogo);
            if (tipo === 'native') dialogo.showModal();
            document.querySelector('#cerrar').focus();
        }
        """, tipo);

    private Task<string[]> LlamadasAsync() => _page.EvaluateAsync<string[]>("llamadas.map(([metodo, tecla]) => `${metodo}:${tecla}`)");

    public async Task DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}
