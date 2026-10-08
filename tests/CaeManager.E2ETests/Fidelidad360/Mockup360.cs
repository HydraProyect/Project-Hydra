using System.Text.Json;
using Microsoft.Playwright;

namespace CaeManager.E2ETests.Fidelidad360;

/// <summary>
/// Un mockup de ficha 360: qué fichero es, cómo se deja en el tema pedido y con qué
/// selectores se leen sus piezas. Los mockups no viven en este repositorio (son documentación,
/// en Project-Hydra-Negocio/tecnico/docs/blueprints/mockups), así que el directorio llega
/// por la variable de entorno <see cref="VariableDirectorio"/> y sin ella las pruebas que
/// los necesitan se declaran omitidas, nunca verdes.
/// </summary>
public sealed record Mockup360(
    string Fichero, string EsperarSelector, SelectoresDeLado Selectores, string? GuionPreparar, Func<string, string?> GuionTema)
{
    public const string VariableDirectorio = "FIDELIDAD360_MOCKUPS";
    public const string VariableSalida = "FIDELIDAD360_SALIDA";

    /// <summary>Origen ficticio: Playwright responde sus peticiones desde disco, sin servidor.</summary>
    private const string Origen = "http://mockups.fidelidad360.test";

    public static string? Directorio =>
        Environment.GetEnvironmentVariable(VariableDirectorio) is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    public static string DirectorioSalida =>
        Environment.GetEnvironmentVariable(VariableSalida) is { Length: > 0 } d ? d : Path.Combine(AppContext.BaseDirectory, "fidelidad-360");

    /// <summary>
    /// Mockup «… 360 página TALVEG.dc.html» anterior a la convención <c>data-pieza</c>
    /// (Empresa y Cliente empresarial): plantilla que <c>support.js</c> pinta en cliente, con
    /// las clases de Centro 360. No define tema oscuro: se le pide igualmente y
    /// <see cref="Fidelidad360.TemaAplicado"/> dirá que no cambió nada.
    /// </summary>
    public static Mockup360 PaginaDc(string fichero) => new(
        fichero,
        ".centro360-cabecera",
        SelectoresDeLado.Con("body", new Dictionary<string, string>
        {
            [SelectoresDeLado.Pastilla] = ".badge",
            [SelectoresDeLado.Tarjeta] = ".centro360-tarjeta-lateral",
            [SelectoresDeLado.Lateral] = "aside.centro360-lateral",
            [SelectoresDeLado.Anillo] = "[class^=\"anillo-\"][role=\"img\"]",
            [SelectoresDeLado.Cabecera] = ".centro360-cabecera",
            [SelectoresDeLado.Fila] = ".fila-rel",
            [SelectoresDeLado.FilaDetalle] = ".fila-rel-sub",
        }),
        GuionPreparar: null,
        GuionTema: tema => tema == "oscuro" ? "document.documentElement.dataset.theme = 'dark'" : "delete document.documentElement.dataset.theme");

    /// <summary>
    /// Una de las cinco páginas de «Paginas 360 nuevas TALVEG.maqueta.html»
    /// (<c>sub</c>, <c>veh</c>, <c>tipo</c>, <c>pro</c>, <c>vis</c>), que es hoy la única
    /// maqueta con los valores decididos el 2026-10-08.
    /// </summary>
    public static Mockup360 MaquetaCombinada(string pagina) => new(
        "Paginas 360 nuevas TALVEG.maqueta.html",
        "#mock .tarjeta",
        SelectoresDeLado.Con("#mock", new Dictionary<string, string>
        {
            [SelectoresDeLado.Pastilla] = ".pill",
            [SelectoresDeLado.Tarjeta] = ".tarjeta",
            [SelectoresDeLado.Lateral] = ".lateral",
            [SelectoresDeLado.Anillo] = ".anillo",
            [SelectoresDeLado.Cabecera] = ".ident",
            [SelectoresDeLado.Fila] = ".fila",
            [SelectoresDeLado.FilaDetalle] = ".fila .two small",
            [SelectoresDeLado.FilaProblemaPeligro] = ".fila.hot",
            [SelectoresDeLado.FilaProblemaAdvertencia] = ".fila.warm",
        }),
        GuionPreparar: $"document.querySelector('#nav button[data-p=\"{pagina}\"]').click()",
        GuionTema: tema => $"document.querySelector('#s-tema button[data-v=\"{tema}\"]').click()");

    /// <summary>Mockup nuevo que ya marca sus piezas con <c>data-pieza</c>.</summary>
    public static Mockup360 PorConvencion(string fichero, string esperarSelector, Func<string, string?> guionTema) =>
        new(fichero, esperarSelector, SelectoresDeLado.Convencion(), GuionPreparar: null, guionTema);

    public async Task AbrirAsync(IPage page, string directorio, string tema)
    {
        await page.Context.RouteAsync(Origen + "/**", async ruta =>
        {
            var relativa = Uri.UnescapeDataString(new Uri(ruta.Request.Url).AbsolutePath).TrimStart('/');
            var completa = Path.GetFullPath(Path.Combine(directorio, relativa));
            var dentro = completa.StartsWith(Path.GetFullPath(directorio) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (dentro && File.Exists(completa))
                await ruta.FulfillAsync(new RouteFulfillOptions { Path = completa });
            else
                await ruta.FulfillAsync(new RouteFulfillOptions { Status = 404 });
        });

        await page.GotoAsync($"{Origen}/{Uri.EscapeDataString(Fichero)}");
        if (GuionPreparar is not null)
            await page.EvaluateAsync(GuionPreparar);
        if (GuionTema(tema) is { } guion)
            await page.EvaluateAsync(guion);
        await page.WaitForSelectorAsync(EsperarSelector, new PageWaitForSelectorOptions { Timeout = 30_000 });
        await Fidelidad360.AsentarAsync(page);
    }
}

/// <summary>Lo que rodea a la medición: contexto de tamaño fijo, tema, capturas e indicador de píxeles.</summary>
public static class Fidelidad360
{
    public const int Ancho = 1440;
    public const int Alto = 900;
    public static readonly string[] Temas = ["claro", "oscuro"];

    public static Task<IBrowserContext> NuevoContextoAsync(IBrowser navegador) =>
        navegador.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = Ancho, Height = Alto },
            DeviceScaleFactor = 1,
            ReducedMotion = ReducedMotion.Reduce,
        });

    /// <summary>
    /// Pone el tema en la ficha del producto por su atributo, que es de lo único que cuelgan
    /// los tokens (<c>:root[data-theme='oscuro']</c>): no toca la preferencia de la cuenta,
    /// que es compartida por toda la colección.
    /// </summary>
    public static async Task PonerTemaDeFichaAsync(IPage page, string tema)
    {
        await page.EvaluateAsync("t => document.documentElement.setAttribute('data-theme', t)", tema);
        await AsentarAsync(page);
        Assert.Equal(tema, await page.EvaluateAsync<string>("() => document.documentElement.getAttribute('data-theme')"));
    }

    /// <summary>Fuentes cargadas y dos fotogramas pintados.</summary>
    public static Task AsentarAsync(IPage page) => page.EvaluateAsync(
        "async () => { await document.fonts.ready; await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))); }");

    /// <summary>
    /// Mide hasta que dos lecturas seguidas coinciden: una ficha Blazor sigue pintando listas
    /// después de la navegación, y una medición a medio pintar da diferencias que no existen.
    /// </summary>
    public static async Task<Medicion> MedirAsentadoAsync(IPage page, string lado, SelectoresDeLado selectores)
    {
        var anterior = await MedidorPiezas360.MedirAsync(page, lado, selectores);
        for (var intento = 0; intento < 20; intento++)
        {
            await page.WaitForTimeoutAsync(400);
            var actual = await MedidorPiezas360.MedirAsync(page, lado, selectores);
            if (actual.Magnitudes.Count > 0 && Iguales(anterior, actual))
                return actual;
            anterior = actual;
        }

        throw new Xunit.Sdk.XunitException($"«{lado}» no se asentó: dos mediciones seguidas nunca coincidieron en 8 s.");
    }

    private static bool Iguales(Medicion a, Medicion b) =>
        a.Magnitudes.Count == b.Magnitudes.Count
        && a.Magnitudes.All(m => b.Magnitudes.TryGetValue(m.Key, out var otra) && otra == m.Value);

    /// <summary>
    /// ¿Cambió algo al pedir el tema oscuro? Un lado sin tema oscuro devuelve lo mismo en los
    /// dos temas, y compararlo en oscuro daría una tabla de diferencias de tema, no de diseño.
    /// </summary>
    public static bool TemaAplicado(Medicion claro, Medicion oscuro) =>
        claro.Magnitudes.TryGetValue("fondo-pagina.background-color", out var a)
        && oscuro.Magnitudes.TryGetValue("fondo-pagina.background-color", out var b)
        && a.Valor != b.Valor;

    public static async Task CapturarAsync(IPage page, string ruta)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = ruta, Animations = ScreenshotAnimations.Disabled });
    }

    /// <summary>
    /// Fracción de píxeles que difieren entre dos capturas del mismo tamaño (umbral de 16
    /// por canal) y una imagen con esos píxeles en rojo. Es un indicador, no un veredicto:
    /// con datos de ejemplo distintos a cada lado nunca baja a cero.
    /// </summary>
    public static async Task<double> DiferenciaDePixelesAsync(IBrowserContext contexto, string capturaA, string capturaB, string rutaDiferencia)
    {
        var page = await contexto.NewPageAsync();
        try
        {
            var resultado = await page.EvaluateAsync<JsonElement>(
                """
                async ([a, b]) => {
                  const cargar = src => new Promise((ok, mal) => { const i = new Image(); i.onload = () => ok(i); i.onerror = mal; i.src = 'data:image/png;base64,' + src; });
                  const [ia, ib] = [await cargar(a), await cargar(b)];
                  if (ia.width !== ib.width || ia.height !== ib.height) return { fraccion: 1, png: '' };
                  const datos = img => { const c = document.createElement('canvas'); c.width = img.width; c.height = img.height; const x = c.getContext('2d'); x.drawImage(img, 0, 0); return [c, x, x.getImageData(0, 0, img.width, img.height)]; };
                  const [lienzo, ctx, da] = datos(ia), [, , db] = datos(ib);
                  let distintos = 0;
                  for (let i = 0; i < da.data.length; i += 4) {
                    const difiere = Math.abs(da.data[i] - db.data[i]) > 16 || Math.abs(da.data[i + 1] - db.data[i + 1]) > 16 || Math.abs(da.data[i + 2] - db.data[i + 2]) > 16;
                    if (difiere) { distintos++; da.data[i] = 255; da.data[i + 1] = 0; da.data[i + 2] = 0; }
                    else { da.data[i] = (da.data[i] + 510) / 3; da.data[i + 1] = (da.data[i + 1] + 510) / 3; da.data[i + 2] = (da.data[i + 2] + 510) / 3; }
                  }
                  ctx.putImageData(da, 0, 0);
                  return { fraccion: distintos / (da.data.length / 4), png: lienzo.toDataURL('image/png').split(',')[1] };
                }
                """,
                new[] { Convert.ToBase64String(await File.ReadAllBytesAsync(capturaA)), Convert.ToBase64String(await File.ReadAllBytesAsync(capturaB)) });

            if (resultado.GetProperty("png").GetString() is { Length: > 0 } png)
                await File.WriteAllBytesAsync(rutaDiferencia, Convert.FromBase64String(png));
            return resultado.GetProperty("fraccion").GetDouble();
        }
        finally
        {
            await page.CloseAsync();
        }
    }
}

/// <summary>Teoría que solo corre si <see cref="Mockup360.VariableDirectorio"/> apunta a los mockups.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TeoriaConMockupsAttribute : TheoryAttribute
{
    public TeoriaConMockupsAttribute()
    {
        if (Mockup360.Directorio is null)
            Skip = $"Necesita los mockups: define {Mockup360.VariableDirectorio} con la carpeta de mockups de Project-Hydra-Negocio.";
    }
}

/// <summary>Chromium sin aplicación, para las pruebas del propio comparador.</summary>
public sealed class NavegadorFidelidadFixture : IAsyncLifetime
{
    private const string ExecutablePathChromium = "/opt/pw-browsers/chromium";
    private IPlaywright? _playwright;

    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            ExecutablePath = File.Exists(ExecutablePathChromium) ? ExecutablePathChromium : null,
            Headless = true,
        });
    }

    public async Task DisposeAsync()
    {
        await Browser.DisposeAsync();
        _playwright?.Dispose();
    }
}

/// <summary>
/// Instancia con la siembra de escenarios de dirección (Cyberdyne Ibérica S.A., Montajes
/// Skynet S.L., Transportes Terminator S.L.…, los nombres de las maquetas) y la siembra
/// propia de las fichas 360 nuevas.
/// </summary>
[CollectionDefinition("AppCollectionFichas360")]
public class AppCollectionFichas360 : ICollectionFixture<WebAppFixtureFichas360>;

public sealed class WebAppFixtureFichas360 : WebAppFixture
{
    protected override IReadOnlyDictionary<string, string> VariablesDeEntornoAdicionales() =>
        new Dictionary<string, string>
        {
            ["DatosPrueba__EscenariosDireccion"] = "true",
            ["DatosPrueba__Fichas360"] = "true",
        };
}
