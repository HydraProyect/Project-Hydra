using System.Runtime.CompilerServices;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Traza de Playwright (capturas, instantáneas del DOM y acciones) que solo se conserva si el test falla (#2194). Se
/// declara justo después del contexto, para que se libere antes que él, y el test llama a <see cref="Superada"/> en
/// su última línea:
/// <code>
/// await using var contexto = await fixture.Browser.NewContextAsync();
/// await using var traza = await TrazaAlFallar.IniciarAsync(contexto);
/// ...
/// traza.Superada();
/// </code>
/// Si el test no llega a esa línea (una aserción o una excepción lo cortan), la traza se escribe en
/// tests/CaeManager.E2ETests/TestResults/trazas-playwright/, que el paso «Publicar logs de CaeManager.Web si el E2E
/// falla» de ci.yml ya sube como artefacto (sube TestResults entero). Se abre con
/// <c>pwsh bin/Debug/net10.0/playwright.ps1 show-trace &lt;zip&gt;</c> o en trace.playwright.dev.
/// </summary>
public sealed class TrazaAlFallar : IAsyncDisposable
{
    private readonly IBrowserContext _contexto;
    private readonly string _nombre;
    private bool _superada;

    private TrazaAlFallar(IBrowserContext contexto, string nombre)
    {
        _contexto = contexto;
        _nombre = nombre;
    }

    public static async Task<TrazaAlFallar> IniciarAsync(
        IBrowserContext contexto, [CallerMemberName] string prueba = "", [CallerFilePath] string archivo = "")
    {
        var nombre = $"{Path.GetFileNameWithoutExtension(archivo)}.{prueba}";
        await contexto.Tracing.StartAsync(new TracingStartOptions { Title = nombre, Screenshots = true, Snapshots = true });
        return new TrazaAlFallar(contexto, nombre);
    }

    /// <summary>El test llegó al final: la traza se descarta.</summary>
    public void Superada() => _superada = true;

    /// <summary>Ruta en la que se escribe la traza de un test fallido.</summary>
    public static string Directorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir is null
            ? Path.Combine(AppContext.BaseDirectory, "TestResults", "trazas-playwright")
            : Path.Combine(dir, "tests", "CaeManager.E2ETests", "TestResults", "trazas-playwright");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_superada)
            {
                await _contexto.Tracing.StopAsync();
                return;
            }

            var directorio = Directorio();
            Directory.CreateDirectory(directorio);
            var ruta = Path.Combine(directorio, $"{_nombre}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
            await _contexto.Tracing.StopAsync(new TracingStopOptions { Path = ruta });
            Console.WriteLine($"Traza de Playwright del test fallido: {ruta}");
        }
        catch (Exception excepcion) when (excepcion is PlaywrightException or IOException or UnauthorizedAccessException)
        {
            // El contexto o el navegador ya se cerraron (p. ej. el test falló porque se cayó el navegador) o no se pudo
            // escribir el fichero: tapar con esto la excepción original del test sería peor que perder la traza.
        }
    }
}
