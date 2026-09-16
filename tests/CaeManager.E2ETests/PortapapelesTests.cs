using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>JS real en Chromium; dobles solo en los permisos del portapapeles, sin Blazor ni base de datos.</summary>
public class PortapapelesTests : IAsyncLifetime
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
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx"))) directorio = directorio.Parent;
        var raiz = directorio?.FullName ?? throw new InvalidOperationException("No se encontró el árbol fuente.");
        await _page.RouteAsync("https://talveg.test/**", async ruta =>
        {
            var esJs = new Uri(ruta.Request.Url).AbsolutePath.EndsWith("clipboard.js", StringComparison.Ordinal);
            await ruta.FulfillAsync(new()
            {
                ContentType = esJs ? "text/javascript" : "text/html",
                Body = esJs ? await File.ReadAllTextAsync(Path.Combine(raiz, "src", "CaeManager.Web", "wwwroot", "js", "clipboard.js"))
                    : "<html lang='es'><body><input id='editor' value='abcdef' aria-label='Texto'><input id='manual' readonly value='02/08/2026' aria-label='Fecha completa'></body></html>"
            });
        });
        await _page.GotoAsync("https://talveg.test/");
        await _page.EvaluateAsync("async () => { window.modulo = await import('/clipboard.js'); editor.focus(); editor.setSelectionRange(1, 4); }");
    }

    [Theory]
    [InlineData("api")]
    [InlineData("respaldo")]
    [InlineData("sin-api")]
    [InlineData("denegado")]
    [InlineData("excepcion")]
    [InlineData("dialogo")]
    public async Task La_copia_respeta_el_resultado_y_limpia_el_respaldo_sin_perder_foco_ni_seleccion(string modo)
    {
        await _page.EvaluateAsync("""
            modo => {
                window.llamadas = [];
                Object.defineProperty(navigator, 'clipboard', { configurable: true, value: modo === 'sin-api' ? undefined : {
                    writeText: async texto => { llamadas.push('api:' + texto); if (modo !== 'api') throw new DOMException('Denegado', 'NotAllowedError'); }
                }});
                document.execCommand = () => {
                    const campo = document.activeElement;
                    llamadas.push('respaldo:' + campo.value.substring(campo.selectionStart, campo.selectionEnd));
                    if (modo === 'excepcion') throw new Error('Respaldo denegado');
                    return modo !== 'denegado';
                };
                if (modo === 'dialogo') {
                    const dialogo = document.createElement('dialog');
                    document.body.append(dialogo);
                    dialogo.append(editor);
                    dialogo.showModal();
                    editor.focus(); editor.setSelectionRange(1, 4);
                }
            }
            """, modo);
        var resultado = await _page.EvaluateAsync<string>("async () => { try { await modulo.copiarAlPortapapeles('02/08/2026'); return 'copiado'; } catch { return 'denegado'; } }");
        Assert.Equal(modo is "denegado" or "excepcion" ? "denegado" : "copiado", resultado);
        var llamadas = await _page.EvaluateAsync<string[]>("llamadas");
        Assert.Equal(modo switch
        {
            "api" => ["api:02/08/2026"],
            "sin-api" => ["respaldo:02/08/2026"],
            _ => new[] { "api:02/08/2026", "respaldo:02/08/2026" }
        }, llamadas);
        await Assertions.Expect(_page.Locator("textarea")).ToHaveCountAsync(0);
        await Assertions.Expect(_page.Locator("#editor")).ToBeFocusedAsync();
        Assert.Equal("bcd", await _page.EvaluateAsync<string>("editor.value.substring(editor.selectionStart, editor.selectionEnd)"));
    }

    [Fact]
    public async Task El_respaldo_conserva_la_seleccion_de_un_texto_editable()
    {
        var seleccionado = await _page.EvaluateAsync<string>("""
            async () => {
                Object.defineProperty(navigator, 'clipboard', { configurable: true, value: undefined });
                document.execCommand = () => true;
                const editor = document.createElement('div');
                editor.id = 'comentario'; editor.contentEditable = 'true'; editor.textContent = 'abcdef';
                document.body.append(editor); editor.focus();
                const rango = document.createRange(); rango.setStart(editor.firstChild, 1); rango.setEnd(editor.firstChild, 4);
                getSelection().removeAllRanges(); getSelection().addRange(rango);
                await modulo.copiarAlPortapapeles('02/08/2026');
                return getSelection().toString();
            }
            """);
        Assert.Equal("bcd", seleccionado);
        await Assertions.Expect(_page.Locator("#comentario")).ToBeFocusedAsync();
    }

    [Fact]
    public async Task La_recuperacion_manual_selecciona_la_fecha_completa()
    {
        await _page.EvaluateAsync("modulo.seleccionarFechaParaCopiaManual(manual)");
        await Assertions.Expect(_page.Locator("#manual")).ToBeFocusedAsync();
        Assert.Equal("02/08/2026", await _page.EvaluateAsync<string>("manual.value.substring(manual.selectionStart, manual.selectionEnd)"));
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}
