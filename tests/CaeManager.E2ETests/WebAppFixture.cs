using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Arranca CaeManager.Web como un proceso real (el mismo binario que se
/// despliega, no un servidor in-memory) contra una base de datos PostgreSQL
/// temporal (propia, borrada al terminar — ver BaseDatosPostgresDePruebas) y
/// sembrada con datos de prueba (ver DatosPruebaSeeder/IdentitySeeder). Se
/// comparte entre todas las clases de test de la colección "AppCollection" —
/// un solo arranque (migraciones + siembra de ~2000 filas) para toda la
/// suite, no uno por clase. Necesita un servidor PostgreSQL accesible (local
/// o el servicio de CI, ver BaseDatosPostgresDePruebas).
/// </summary>
public class WebAppFixture : IAsyncLifetime
{
    private const string ExecutablePathChromium = "/opt/pw-browsers/chromium";

    private static readonly Lock CandadoInstalacion = new();
    private static bool _navegadoresInstalados;

    private Process? _proceso;
    private IPlaywright? _playwright;
    private string? _cadenaConexion;

    public string BaseUrl { get; private set; } = string.Empty;

    public IBrowser Browser { get; private set; } = null!;

    /// <summary>
    /// Punto de extensión para subclases (ver
    /// <see cref="WebAppFixtureConSegundoTenant"/>) que necesitan variables
    /// de entorno adicionales al arrancar el proceso real de
    /// CaeManager.Web — sin tocar el comportamiento de la fixture
    /// compartida por "AppCollection".
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string> VariablesDeEntornoAdicionales() =>
        new Dictionary<string, string>();

    public async Task InitializeAsync()
    {
        var puerto = ObtenerPuertoLibre();
        BaseUrl = $"http://127.0.0.1:{puerto}";

        _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica("e2e");

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
        infoInicio.Environment["ConnectionStrings__CaeManagerDb"] = _cadenaConexion;
        infoInicio.Environment["DatosPrueba__Activo"] = "true";

        // Horizonte 1.6 (ciclo documental E2E): registra ProveedorFalsoDocumentAI
        // como IDocumentAIProvider primario (ver InfrastructureServiceCollectionExtensions)
        // en vez de depender de Anthropic/Gemini/Mistral reales — que en este
        // proceso son "inertes" de todos modos (sin ApiKey, ver
        // AnthropicDocumentAIProvider) y, aunque tuvieran clave, harían de un
        // test de flujo de aplicación algo no determinista, de pago y lento.
        // Inerte para el resto de la suite: solo se enciende algo si un test
        // activa VerificacionIaActiva en un TipoDocumento (ningún dato
        // sembrado lo trae activo por defecto, ver TipoDocumentoSeedData),
        // así que compartirlo en "AppCollection" no afecta a ningún otro test.
        infoInicio.Environment["DocumentosIa__ProveedorFalsoActivo"] = "true";

        // El rate limiting de /cuenta/* (P0-2: fuerza bruta) limita los POST
        // anónimos a 10/min POR IP — y toda esta suite llega desde 127.0.0.1
        // haciendo logins reales en serie, donde cada login del Administrador
        // son DOS POST anónimos (credenciales + código 2FA). La colección
        // "AppCollection" sola acumula ~13 POST anónimos en menos de un
        // minuto: el POST nº 11 recibía un 429 silencioso, el navegador se
        // quedaba en la página de login y el test moría 30 s después
        // esperando ".nav-principal" — el cuelgue intermitente de la Fase 69
        // (intermitente porque dependía de dónde cayera el corte de la
        // ventana fija de 1 minuto; por eso los tests pasaban aislados y
        // fallaban solo dentro de la suite completa). Techo alto vía
        // configuración (ver Program.cs, valores por defecto intactos en
        // producción): aquí el "ataque" es el propio runner.
        infoInicio.Environment["RateLimiting__Cuenta__LimiteAnonimo"] = "1000";
        infoInicio.Environment["RateLimiting__Cuenta__LimiteAutenticado"] = "1000";

        foreach (var (clave, valor) in VariablesDeEntornoAdicionales())
            infoInicio.Environment[clave] = valor;

        _proceso = Process.Start(infoInicio)
            ?? throw new InvalidOperationException("No se pudo arrancar el proceso de CaeManager.Web.");

        // stderr se reenvía (prefijado con el puerto, para distinguir las 4
        // fixtures que pueden estar corriendo en la misma suite) en vez de
        // descartarse: una excepción no controlada mientras la app ya está
        // arrancada y sirviendo peticiones no dejaba ningún rastro en el log
        // de CI — un test E2E que falla por timeout esperando contenido daba
        // exactamente el mismo síntoma tanto si la página tardaba de más
        // como si el servidor había reventado al renderizarla, y no había
        // forma de distinguirlos. stdout NO se reenvía aquí para no ahogar la
        // salida del runner, pero eso no significa que se pierda: Serilog
        // escribe lo mismo en su sink de archivo (ver Program.cs,
        // "Logging:RutaArchivo"), que con el content root de estos tests cae
        // en src/CaeManager.Web/bin/{Configuration}/net10.0/App_Data/logs/
        // log-AAAAMMDD.txt. Ese fichero es el sitio donde mirar cuando un
        // test E2E se queda esperando algo que nunca llega: un comando que
        // revienta en un circuito de Blazor ya cerrado solo deja rastro ahí
        // (así se encontró la causa del fallo del Paso 0 de
        // FlujoCicloDocumentalTests). Leer los buffers con BeginOutputReadLine
        // sigue siendo necesario aunque no se escriba nada: evita que se
        // llenen y bloqueen al proceso hijo.
        _proceso.OutputDataReceived += (_, _) => { };
        _proceso.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                Console.Error.WriteLine($"[CaeManager.Web:{puerto}] {e.Data}");
        };
        _proceso.BeginOutputReadLine();
        _proceso.BeginErrorReadLine();

        await EsperarArranqueAsync();

        _playwright = await Playwright.CreateAsync();
        Browser = await LanzarChromiumAsync();
    }

    /// <summary>
    /// Lanza Chromium y, si no está instalado, lo instala y reintenta una vez.
    ///
    /// En CI los navegadores los pone un paso explícito del workflow
    /// (ver ci.yml, "Instalar navegadores de Playwright"), así que este camino
    /// no se usa allí. Existe para la máquina de un desarrollador recién
    /// clonada: sin esto, <c>dotnet test</c> da 8 rojos con un
    /// "Executable doesn't exist at ..." que no dice qué hacer.
    /// </summary>
    private async Task<IBrowser> LanzarChromiumAsync()
    {
        // ExecutablePathChromium solo existe en el sandbox de este entorno de
        // desarrollo — en CI (y en cualquier otra máquina) el Chromium real
        // vive donde lo puso "playwright install", la caché estándar de
        // Playwright. Se usa el path fijo solo si de verdad está ahí; si no,
        // se deja sin ExecutablePath para que Playwright resuelva el suyo.
        var opciones = new BrowserTypeLaunchOptions
        {
            ExecutablePath = File.Exists(ExecutablePathChromium) ? ExecutablePathChromium : null,
            Headless = true,
        };

        try
        {
            return await _playwright!.Chromium.LaunchAsync(opciones);
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.Ordinal))
        {
            InstalarNavegadores();
            return await _playwright!.Chromium.LaunchAsync(opciones);
        }
    }

    private static void InstalarNavegadores()
    {
        // Serializado: WebAppFixtureConSegundoTenant es otra fixture que puede
        // inicializarse en paralelo con esta, y dos instalaciones simultáneas
        // sobre la misma caché se pisarían.
        lock (CandadoInstalacion)
        {
            // Otra fixture pudo instalarlos mientras esperábamos el candado:
            // las dos fallan al lanzar antes de que ninguna llegue a instalar.
            if (_navegadoresInstalados) return;

            Console.WriteLine(
                "Navegadores de Playwright no encontrados. Descargando chromium (~300 MB, solo la primera vez)...");

            // Mismo instalador que invoca playwright.ps1, pero en proceso: no
            // depende de que exista pwsh en la máquina. Sin --with-deps a
            // propósito — eso necesita sudo en Linux y en CI ya se hace aparte.
            var codigoSalida = Microsoft.Playwright.Program.Main(["install", "chromium"]);

            if (codigoSalida != 0)
                throw new InvalidOperationException(
                    $"La instalación automática de los navegadores de Playwright falló (código {codigoSalida}). "
                    + "Instálalos a mano con: pwsh tests/CaeManager.E2ETests/bin/Debug/net10.0/playwright.ps1 install chromium");

            _navegadoresInstalados = true;
        }
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

        if (_cadenaConexion is null) return;

        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
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
    ///
    /// Presupuesto de 120s: tres sesiones midieron el arranque en frío por
    /// separado el 2026-08-28 y rondaba los 63s contra un presupuesto de 60s
    /// — producía fallos rojos con traza en InitializeAsync que no eran del
    /// código bajo prueba (en caliente los mismos tests pasan en 3-4s). El
    /// margen no penaliza el camino feliz: el sondeo devuelve en cuanto /salud
    /// responde 200, así que un techo más alto solo importa cuando el arranque
    /// ya iba lento.
    /// </summary>
    private async Task EsperarArranqueAsync()
    {
        using var cliente = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var limite = DateTime.UtcNow.AddSeconds(120);

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

/// <summary>
/// Arranca CaeManager.Web con un segundo tenant sembrado (ver
/// SegundoTenantSeeder) para poder verificar el aislamiento multi-tenant
/// con un navegador real (PLAN-MIGRACION-MULTITENANT.md § 6, Etapa 5). En
/// una colección propia — no "AppCollection" — para no forzar el sembrado
/// del segundo tenant en el resto de la suite E2E.
/// </summary>
public sealed class WebAppFixtureConSegundoTenant : WebAppFixture
{
    protected override IReadOnlyDictionary<string, string> VariablesDeEntornoAdicionales() =>
        new Dictionary<string, string> { ["SegundoTenant__Activo"] = "true" };
}

[CollectionDefinition("AppCollectionMultiTenant")]
public class AppCollectionMultiTenant : ICollectionFixture<WebAppFixtureConSegundoTenant>;

/// <summary>
/// Arranca CaeManager.Web con la política de retención activa
/// (RetencionDatos:Activa, apagada por defecto en cualquier otro sitio —
/// ver CLAUDE.md) para poder ejercitar /retencion de verdad con Playwright
/// (P1-19 de docs/business/MATURITY_REVIEW.md). En su propia colección: el
/// resto de la suite E2E no necesita ni debe activar retención.
/// </summary>
public sealed class WebAppFixtureConRetencionActiva : WebAppFixture
{
    protected override IReadOnlyDictionary<string, string> VariablesDeEntornoAdicionales() =>
        new Dictionary<string, string> { ["RetencionDatos__Activa"] = "true" };
}

[CollectionDefinition("AppCollectionRetencion")]
public class AppCollectionRetencion : ICollectionFixture<WebAppFixtureConRetencionActiva>;

/// <summary>
/// Instancia propia (sin variables de entorno extra) solo para
/// FlujoSoporteTests: abrir un acceso de soporte crea una
/// AsignacionOperadorDelegado nueva hacia el Cliente Delegante de demo que
/// sigue existiendo después de cerrar el acceso (cerrar solo desactiva la
/// DelegacionTenant, no borra la asignación) — con "AppCollection"
/// compartida, esa segunda asignación deja dos &lt;option&gt; con el mismo
/// texto en el selector de Cliente activo y rompe
/// Ayudas.CambiarClienteActivoAsync para el resto de tests de esa
/// colección (AlcanceRolesTests, FlujoDelegatedWorkspaceTests) — visto en
/// CI, no una hipótesis.
/// </summary>
[CollectionDefinition("AppCollectionSoporte")]
public class AppCollectionSoporte : ICollectionFixture<WebAppFixtureParaSoporte>;

public sealed class WebAppFixtureParaSoporte : WebAppFixture;
