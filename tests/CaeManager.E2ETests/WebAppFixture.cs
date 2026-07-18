using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Arranca CaeManager.Web como un proceso real (el mismo binario que se
/// despliega, no un servidor in-memory) contra una base de datos SQLite
/// temporal y sembrada con datos de prueba (ver
/// DatosPruebaSeeder/IdentitySeeder). Se comparte entre todas las clases de
/// test de la colección "AppCollection" — un solo arranque (migraciones +
/// siembra de ~1500 filas) para toda la suite, no uno por clase.
/// </summary>
public sealed class WebAppFixture : IAsyncLifetime
{
    private const string ExecutablePathChromium = "/opt/pw-browsers/chromium";

    private Process? _proceso;
    private IPlaywright? _playwright;
    private string? _rutaBaseDatos;

    public string BaseUrl { get; private set; } = string.Empty;

    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var puerto = ObtenerPuertoLibre();
        BaseUrl = $"http://127.0.0.1:{puerto}";

        _rutaBaseDatos = Path.Combine(Path.GetTempPath(), $"caemanager-e2e-{Guid.NewGuid():N}.db");

        var rutaDll = LocalizarCaeManagerWebDll();

        var infoInicio = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{rutaDll}\"",
            WorkingDirectory = Path.GetDirectoryName(rutaDll),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        infoInicio.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        infoInicio.Environment["ASPNETCORE_URLS"] = BaseUrl;
        infoInicio.Environment["ConnectionStrings__CaeManagerDb"] = $"Data Source={_rutaBaseDatos}";
        infoInicio.Environment["DatosPrueba__Activo"] = "true";

        _proceso = Process.Start(infoInicio)
            ?? throw new InvalidOperationException("No se pudo arrancar el proceso de CaeManager.Web.");

        // No dejamos que los buffers de stdout/stderr se llenen y bloqueen al
        // proceso hijo — se descartan, pero si arrancar falla el mensaje de
        // EsperarArranqueAsync (timeout) sigue siendo diagnosticable desde ahí.
        _proceso.OutputDataReceived += (_, _) => { };
        _proceso.ErrorDataReceived += (_, _) => { };
        _proceso.BeginOutputReadLine();
        _proceso.BeginErrorReadLine();

        await EsperarArranqueAsync();

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            // ExecutablePathChromium solo existe en el sandbox de este
            // entorno de desarrollo — en CI (y en cualquier otra máquina)
            // el Chromium real vive donde lo puso "playwright install"
            // (ver ci.yml), la caché estándar de Playwright. Se usa el path
            // fijo solo si de verdad está ahí; si no, se deja sin
            // ExecutablePath para que Playwright resuelva el suyo.
            ExecutablePath = File.Exists(ExecutablePathChromium) ? ExecutablePathChromium : null,
            Headless = true,
        });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
            await Browser.CloseAsync();

        _playwright?.Dispose();

        if (_proceso is { HasExited: false })
        {
            try
            {
                _proceso.Kill(entireProcessTree: true);
                await _proceso.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
                // El proceso ya había terminado entre la comprobación y el Kill.
            }
        }

        _proceso?.Dispose();

        if (_rutaBaseDatos is null) return;

        foreach (var sufijo in new[] { string.Empty, "-shm", "-wal" })
        {
            var ruta = _rutaBaseDatos + sufijo;
            if (File.Exists(ruta)) File.Delete(ruta);
        }
    }

    /// <summary>
    /// Bind a puerto 0 deja al sistema operativo asignar un puerto TCP libre
    /// — se lee y se libera antes de arrancar la app real, evitando
    /// colisiones con cualquier otra instancia corriendo en la máquina.
    /// </summary>
    private static int ObtenerPuertoLibre()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var puerto = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return puerto;
    }

    /// <summary>
    /// La ubicación del .dll de CaeManager.Web se resuelve relativa al
    /// assembly de este proyecto de test (no a un path absoluto fijo) para
    /// que la suite funcione igual en cualquier checkout — sube desde
    /// tests/CaeManager.E2ETests/bin/Debug/net10.0 hasta la raíz del repo y
    /// baja a src/CaeManager.Web/bin/{Configuration}/net10.0.
    /// </summary>
    private static string LocalizarCaeManagerWebDll()
    {
        var directorioTest = AppContext.BaseDirectory;
        var directorio = new DirectoryInfo(directorioTest);

        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;

        if (directorio is null)
            throw new InvalidOperationException($"No se encontró la raíz del repo (CaeManager.slnx) subiendo desde {directorioTest}.");

#if DEBUG
        const string configuracion = "Debug";
#else
        const string configuracion = "Release";
#endif

        var rutaDll = Path.Combine(directorio.FullName, "src", "CaeManager.Web", "bin", configuracion, "net10.0", "CaeManager.Web.dll");

        if (!File.Exists(rutaDll))
            throw new FileNotFoundException(
                $"No se encontró {rutaDll} — compila la solución (dotnet build) antes de correr los tests E2E.", rutaDll);

        return rutaDll;
    }

    /// <summary>
    /// Sondea /salud (endpoint anónimo de Program.cs) hasta obtener 200 — es
    /// la señal real de que las migraciones y la siembra de datos de prueba
    /// (varios cientos de filas) ya terminaron, no solo que el proceso existe.
    /// </summary>
    private async Task EsperarArranqueAsync()
    {
        using var cliente = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var limite = DateTime.UtcNow.AddSeconds(60);

        while (DateTime.UtcNow < limite)
        {
            if (_proceso is { HasExited: true })
                throw new InvalidOperationException(
                    $"El proceso de CaeManager.Web terminó inesperadamente (código {_proceso.ExitCode}) mientras esperábamos que arrancara.");

            try
            {
                var respuesta = await cliente.GetAsync($"{BaseUrl}/salud");
                if (respuesta.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
                // Todavía no acepta conexiones — se reintenta.
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"CaeManager.Web no respondió 200 en {BaseUrl}/salud dentro del tiempo de espera.");
    }
}

[CollectionDefinition("AppCollection")]
public class AppCollection : ICollectionFixture<WebAppFixture>;
