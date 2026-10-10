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

    /// <summary>Las fixtures vivas, por su URL base: <see cref="Ayudas.IniciarSesionAsync"/> solo recibe la URL.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, WebAppFixture> FixturesPorUrl =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Mismo literal que <c>FiltroGuardado.NombreVistaRecordada</c> (CaeManager.Domain) — duplicado aquí
    /// porque este proyecto de test no referencia Domain; si cambia allí, cambia aquí.
    /// </summary>
    private const string NombreVistaRecordada = "__vista_recordada__";

    private Process? _proceso;
    private IPlaywright? _playwright;
    private string? _cadenaConexion;
    private bool _conexionVeTodasLasFilas;

    public string BaseUrl { get; private set; } = string.Empty;

    public IBrowser Browser { get; private set; } = null!;

    /// <summary>
    /// Directorio donde el proceso de CaeManager.Web de ESTA fixture escribe su
    /// log de Serilog (REC-216). Se resuelve desde el .dll que arrancó, no
    /// desde la raíz del repo: es el content root real del proceso.
    /// </summary>
    public string DirectorioLogs { get; private set; } = string.Empty;

    /// <summary>
    /// Patrón de los ficheros de log de esta fixture (uno por día, rotación
    /// de Serilog): <c>log-{TipoDeFixture}-AAAAMMDD.txt</c>.
    /// </summary>
    public string PatronFicheroLog => $"log-{GetType().Name}-*.txt";

    /// <summary>
    /// Punto de extensión para subclases (ver
    /// <see cref="WebAppFixtureConSegundoTenant"/>) que necesitan variables
    /// de entorno adicionales al arrancar el proceso real de
    /// CaeManager.Web — sin tocar el comportamiento de la fixture
    /// compartida por "AppCollection".
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string> VariablesDeEntornoAdicionales() =>
        new Dictionary<string, string>();

    /// <summary>
    /// Punto de extensión para preparar el clúster antes de arrancar el proceso
    /// (ver <see cref="WebAppFixtureBajoRuntime"/>, que da LOGIN al rol
    /// <c>cae_app_runtime</c>). Corre con <see cref="CadenaConexion"/> ya asignada.
    /// </summary>
    protected virtual Task PrepararAntesDeArrancarAsync() => Task.CompletedTask;

    /// <summary>La cadena propietaria de la base de esta fixture.</summary>
    protected string CadenaConexion => _cadenaConexion
        ?? throw new InvalidOperationException("La fixture todavía no ha creado su base.");

    public async Task InitializeAsync()
    {
        var puerto = ObtenerPuertoLibre();
        var tipoFixture = GetType().Name;
        BaseUrl = $"http://127.0.0.1:{puerto}";

        _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica("e2e");
        FixturesPorUrl[BaseUrl] = this;

        var rutaDll = LocalizarCaeManagerWebDll();
        DirectorioLogs = Path.Combine(Path.GetDirectoryName(rutaDll)!, "App_Data", "logs");

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

        // REC-216: un fichero de log propio por fixture. xUnit paraleliza por
        // colección y cada colección arranca su propio proceso de
        // CaeManager.Web; con la ruta por defecto (App_Data/logs/log-.txt)
        // los cuatro procesos escribían en el MISMO fichero, sin puerto,
        // PID ni nombre de test en ninguna línea — medido en el run
        // 35402030148 de CI: cuatro arranques de servidor solapados en un
        // único log-AAAAMMDD.txt de 11.459 líneas, imposible de atribuir a
        // un test. Con una ruta por tipo de fixture (una instancia por
        // colección, ver los CollectionDefinition de abajo) todas las líneas
        // de un fichero salen de un solo proceso, y dentro de una colección
        // los tests son secuenciales: cruzado con las horas del .trx (ver
        // ci.yml, "Tests E2E"), cada línea cae en la ventana de UN test.
        //
        // El nombre empieza por "log-" y vive en el mismo directorio a
        // propósito: Ayudas.EsperarLineaEnLogDeLaAppAsync busca "log-*.txt"
        // sin recursión, y sigue viendo todos los ficheros. Sin puerto en el
        // nombre: retainedFileCountLimit (Program.cs) cuenta por plantilla, y
        // un nombre distinto en cada ejecución nunca rotaría nada en una
        // máquina de desarrollo. La ruta es relativa al content root, igual
        // que la ruta por defecto. Va ANTES del bucle de variables
        // adicionales para que una subclase pueda sobrescribirla si alguna
        // vez lo necesita.
        infoInicio.Environment["Logging__RutaArchivo"] = $"App_Data/logs/log-{tipoFixture}-.txt";

        await PrepararAntesDeArrancarAsync();
        foreach (var (clave, valor) in VariablesDeEntornoAdicionales())
            infoInicio.Environment[clave] = valor;

        _proceso = Process.Start(infoInicio)
            ?? throw new InvalidOperationException("No se pudo arrancar el proceso de CaeManager.Web.");

        // stderr se reenvía (prefijado con el tipo de fixture y el puerto, para
        // distinguir las 4 fixtures que pueden estar corriendo en la misma
        // suite) en vez de
        // descartarse: una excepción no controlada mientras la app ya está
        // arrancada y sirviendo peticiones no dejaba ningún rastro en el log
        // de CI — un test E2E que falla por timeout esperando contenido daba
        // exactamente el mismo síntoma tanto si la página tardaba de más
        // como si el servidor había reventado al renderizarla, y no había
        // forma de distinguirlos. stdout NO se reenvía aquí para no ahogar la
        // salida del runner, pero eso no significa que se pierda: Serilog
        // escribe lo mismo en su sink de archivo (ver Program.cs,
        // "Logging:RutaArchivo", fijada arriba por fixture), que con el
        // content root de estos tests cae en
        // src/CaeManager.Web/bin/{Configuration}/net10.0/App_Data/logs/
        // log-{TipoDeFixture}-AAAAMMDD.txt. Ese fichero es el sitio donde mirar cuando un
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
                Console.Error.WriteLine($"[CaeManager.Web:{tipoFixture}:{puerto}] {e.Data}");
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

    /// <summary>
    /// Escribe directamente en la base de esta instancia, con la cadena de
    /// conexión de la propia fixture. Solo para poner una cuenta de prueba en
    /// un estado que la interfaz no permite fabricar (contraseña temporal sin
    /// cambiar, Administrador sin 2FA).
    /// </summary>
    public async Task EjecutarSqlAsync(string sql, string parametro, string valor)
    {
        var filas = await EjecutarSqlAsync(sql, (parametro, valor));
        Assert.Equal(1, filas);
    }

    /// <summary>Devuelve las filas afectadas; quien llama decide cuántas espera.</summary>
    public async Task<int> EjecutarSqlAsync(string sql, params (string Nombre, string Valor)[] parametros)
    {
        await using var conexion = new Npgsql.NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var (nombre, valor) in parametros)
            comando.Parameters.AddWithValue(nombre, valor);
        return await comando.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Lee un único valor de la base de esta instancia (el id de una fila sembrada,
    /// por ejemplo), con la cadena propietaria de la fixture. Falla si no hay fila.
    /// </summary>
    public async Task<string> LeerValorSqlAsync(string sql, params (string Nombre, string Valor)[] parametros)
    {
        await using var conexion = new Npgsql.NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var (nombre, valor) in parametros)
            comando.Parameters.AddWithValue(nombre, valor);
        return (await comando.ExecuteScalarAsync())?.ToString()
               ?? throw new InvalidOperationException($"La consulta no devolvió ninguna fila: {sql}");
    }

    /// <summary>La fixture que sirve esa URL base, o <c>null</c> si no es ninguna de estas.</summary>
    internal static WebAppFixture? DeLaUrl(string baseUrl) => FixturesPorUrl.GetValueOrDefault(baseUrl);

    /// <summary>
    /// Olvida la vista recordada de todos los listados del Usuario con ese correo, en todos sus Tenants, y
    /// devuelve cuántas tenía. Los filtros guardados con nombre no se tocan.
    ///
    /// <para>
    /// La vista recordada es estado persistente del Usuario y los recorridos comparten cuentas de la siembra:
    /// sin esto, el filtro que dejó puesto un recorrido se le restaura al siguiente que entra con la misma
    /// cuenta en el mismo listado sin parámetros, y deja de ver la lista que esperaba. Medido el 2026-10-10:
    /// la búsqueda que <c>ImportarClientesTests</c> dejó en Clientes tapó el Cliente recién creado de
    /// <c>ClientesListaLeeEmpresasE2ETests</c>. <see cref="Ayudas.IniciarSesionAsync"/> lo llama antes de
    /// entrar, así que cada recorrido empieza con la vista de fábrica; lo que recuerde DENTRO del recorrido
    /// se le sigue restaurando, como a cualquier usuario.
    /// </para>
    ///
    /// <para>
    /// <c>FiltrosGuardados</c> está bajo RLS por Tenant y aquí no hay Tenant fijado: con una conexión que no
    /// se la salte, el borrado no vería ninguna fila y devolvería 0 sin fallar. Se comprueba una vez, y si
    /// no es así se falla en voz alta.
    /// </para>
    /// </summary>
    public async Task<int> OlvidarVistasRecordadasAsync(string email)
    {
        if (!_conexionVeTodasLasFilas)
        {
            var seSaltaRls = await LeerValorSqlAsync(
                "SELECT (rolsuper OR rolbypassrls)::text FROM pg_roles WHERE rolname = current_user");
            if (seSaltaRls != "true")
                throw new InvalidOperationException(
                    "La conexión de la fixture no se salta RLS: no puede olvidar las vistas recordadas entre recorridos.");

            _conexionVeTodasLasFilas = true;
        }

        return await EjecutarSqlAsync(
            """
            DELETE FROM "FiltrosGuardados" f USING "AspNetUsers" u
            WHERE f."UsuarioId" = u."Id" AND u."NormalizedEmail" = upper(@email) AND f."Nombre" = @nombre
            """,
            ("email", email), ("nombre", NombreVistaRecordada));
    }

    public async Task DisposeAsync()
    {
        FixturesPorUrl.TryRemove(BaseUrl, out _);

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
    /// Plazo total de 240s, con 5s por petición y 250ms entre intentos. El
    /// plazo se subió dos veces: de 60s a 120s en #336, porque tres sesiones
    /// midieron el arranque en frío por separado el 2026-08-28 y rondaba los
    /// 63s — producía fallos rojos con traza en InitializeAsync que no eran
    /// del código bajo prueba (en caliente los mismos tests pasan en 3-4s) —,
    /// y de 120s a 240s en #998 (2026-09-29), cuyo mensaje de commit no dice
    /// el motivo. El margen no penaliza el camino feliz: el sondeo devuelve en
    /// cuanto /salud responde 200, así que un techo más alto solo importa
    /// cuando el arranque ya iba lento.
    ///
    /// El bucle vive en <see cref="SondeoDeArranque"/> para poder probarlo
    /// sin proceso ni red; ahí está también qué respuestas se reintentan (la
    /// petición que agota sus 5s, entre ellas). La pausa se escribe aquí y
    /// viaja como delegado.
    /// </summary>
    private async Task EsperarArranqueAsync()
    {
        using var cliente = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var url = $"{BaseUrl}/salud";

        await SondeoDeArranque.EsperarAsync(
            pedirSalud: () => cliente.GetAsync(url),
            codigoDeSalidaSiElProcesoTermino: () => _proceso is { HasExited: true } ? _proceso.ExitCode : null,
            plazo: TimeSpan.FromSeconds(240),
            ahora: () => DateTime.UtcNow,
            pausa: () => Task.Delay(250),
            url: url);
    }
}

[CollectionDefinition("AppCollection")]
public class AppCollection : ICollectionFixture<WebAppFixture>;

/// <summary>
/// Arranca CaeManager.Web con un segundo tenant sembrado (ver
/// SegundoTenantSeeder) para poder verificar el aislamiento multi-tenant
/// con un navegador real (Project-Hydra-Negocio/tecnico/PLAN-MIGRACION-MULTITENANT.md § 6, Etapa 5). En
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
/// (P1-19 de Project-Hydra-Negocio/MATURITY_REVIEW.md). En su propia colección: el
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

/// <summary>
/// Instancia propia (sin variables de entorno extra) para los tests de los
/// listados: fila sin menú, exportar, filtros en la URL, cabeceras, teclado y
/// KeyTips. No existe por aislamiento de datos sino por reloj: "AppCollection"
/// llenaba sola un bloque del job E2E de CI (unos 9 minutos de tests) y era su
/// camino crítico. Con esta mitad aparte, el reparto por colección de
/// scripts/repartir-e2e-por-coleccion.sh las pone en bloques distintos. El
/// coste es un arranque más de la aplicación por run (unos 39 s de runner).
/// Las clases con acoplamientos de datos conocidos entre sí (AlcanceRoles con
/// FlujoDelegatedWorkspace, FlujoCritico con FlujoBandejaPriorizada) y las de
/// autorización y multi-tenancy se quedan en "AppCollection".
/// </summary>
[CollectionDefinition("AppCollectionListados")]
public class AppCollectionListados : ICollectionFixture<WebAppFixtureListados>;

public sealed class WebAppFixtureListados : WebAppFixture;


/// <summary>Instancia propia: los tests de cuenta a medio activar dejan cuentas de prueba en ese estado.</summary>
[CollectionDefinition("AppCollectionCuentaAMedioActivar")]
public class AppCollectionCuentaAMedioActivar : ICollectionFixture<WebAppFixtureCuentaAMedioActivar>;

public sealed class WebAppFixtureCuentaAMedioActivar : WebAppFixture;

/// <summary>
/// Instancia propia con la revalidación del circuito a 2 s (60 s por defecto):
/// el aviso de «la ventana de soporte terminó» dentro de un circuito ya abierto
/// depende de ese ciclo, y esperar un minuto por comprobación no cabe en E2E.
/// </summary>
[CollectionDefinition("AppCollectionVentanaSoporte")]
public class AppCollectionVentanaSoporte : ICollectionFixture<WebAppFixtureVentanaSoporte>;

public sealed class WebAppFixtureVentanaSoporte : WebAppFixture
{
    protected override IReadOnlyDictionary<string, string> VariablesDeEntornoAdicionales() =>
        new Dictionary<string, string> { ["Circuit__RevalidacionIntervaloSegundos"] = "2" };
}

/// <summary>
/// Instancia propia con la siembra del Gestor CAE de un Operador CAE externo con
/// Asignación de Cartera en dos Tenants beneficiarios y un tercero fuera de ella
/// (ver GestorCaeCarteraMultiTenantSeeder). En su colección: el recorrido corrige,
/// crea y cancela datos de esos Tenants, y el resto de la suite no debe ver tres
/// Tenants más en ningún selector.
/// </summary>
[CollectionDefinition("AppCollectionGestorCaeCarteraMultiTenant")]
public class AppCollectionGestorCaeCarteraMultiTenant : ICollectionFixture<WebAppFixtureGestorCaeCarteraMultiTenant>;

public sealed class WebAppFixtureGestorCaeCarteraMultiTenant : WebAppFixture
{
    protected override IReadOnlyDictionary<string, string> VariablesDeEntornoAdicionales() =>
        new Dictionary<string, string>
        {
            ["DatosPrueba__GestorCaeCarteraMultiTenant"] = "true",
            ["Circuit__RevalidacionIntervaloSegundos"] = "2",
        };
}

/// <summary>
/// Instancia propia con la siembra de escenarios de dirección (DatosPrueba:EscenariosDireccion):
/// un Coordinador CAE de un Operador CAE externo que alcanza Pizza Planet y Duff por delegación
/// heredada (AsignacionOperadorDelegado con rol CoordinadorCae): el caso de la cuenta demo del
/// piloto (P0 2026-09-28). En su colección: añade Tenants a todos los selectores.
/// </summary>
[CollectionDefinition("AppCollectionEscenariosDireccion")]
public class AppCollectionEscenariosDireccion : ICollectionFixture<WebAppFixtureEscenariosDireccion>;

public sealed class WebAppFixtureEscenariosDireccion : WebAppFixture
{
    protected override IReadOnlyDictionary<string, string> VariablesDeEntornoAdicionales() =>
        new Dictionary<string, string>
        {
            ["DatosPrueba__EscenariosDireccion"] = "true",
            ["Sesion__IntervaloRevalidacionSegundos"] = "1",
            ["Circuit__RevalidacionIntervaloSegundos"] = "2",
        };
}

/// <summary>
/// Instancia propia para el Encargo de administración (decisión D-8, 2026-10-08), con su base
/// propia: el encargo eleva al Administrador del Operador CAE externo de demo dentro de un Tenant
/// propietario, y eso no debe alcanzar a ninguna otra colección.
///
/// <para>
/// <b>El encargo se siembra aquí, por SQL, y no por la interfaz.</b> En pantalla solo lo registra
/// Soporte TALVEG dentro de una Sesión Privilegiada de aprovisionamiento o un Administrador propio
/// del Tenant propietario, nunca nadie del Operador CAE externo que lo recibe (la política
/// <c>posicion_en_el_encargo</c> lo impide además en la base). Es un estado que quien protagoniza
/// el recorrido no puede fabricar, igual que los de <see cref="WebAppFixture.EjecutarSqlAsync(string, string, string)"/>.
/// La fila copia los dos Tenants y la operación de la Asignación de Operación externa, universal y
/// vigente que ya une al Operador CAE externo con el Tenant propietario: si no hay exactamente una,
/// no se inserta nada y la siembra falla.
/// </para>
///
/// <para>
/// <b>La Asignación de Cartera también se siembra aquí.</b> La siembra de demo da al Administrador
/// del Operador CAE externo una asignación de Operador Delegado (vía heredada) sobre los dos
/// Tenants propietarios, pero ninguna cartera: el relleno de carteras solo emite la de Consulta
/// (<c>AsignacionesOperativasBackfillSeeder.RolesDeAlcanceTotal</c>). Sin cartera entra por la vía
/// heredada, que nunca se eleva, y el encargo no sube ningún techo. La fixture le da una cartera
/// de Gestor CAE <b>del Tenant entero</b> (ámbito universal) bajo cada una de las dos operaciones:
/// el encargo solo eleva una cartera universal, y con la misma cartera en los dos Tenants lo único
/// que los distingue es la fila del encargo. No se toca la siembra de demo de <c>src/</c>.
/// </para>
/// </summary>
[CollectionDefinition("AppCollectionEncargoAdministracion")]
public class AppCollectionEncargoAdministracion : ICollectionFixture<WebAppFixtureEncargoAdministracion>;

public sealed class WebAppFixtureEncargoAdministracion : WebAppFixture
{
    /// <summary>Tenant propietario que encarga su administración al Operador CAE externo de demo.</summary>
    public const string TenantConEncargo = Ayudas.NombreClienteDelegadoDemo2;

    /// <summary>Tenant propietario que el mismo Operador CAE externo opera sin encargo: el control.</summary>
    public const string TenantSinEncargo = Ayudas.NombreClienteDelegadoDemo;

    /// <summary>
    /// Mismo valor que <c>EncargoAdministracion.VersionTextoVigente</c>; se repite porque este
    /// proyecto no referencia Domain (mismo criterio que las constantes de <see cref="Ayudas"/>).
    /// </summary>
    private const string VersionTextoVigente = "2026-10-09";

    private readonly SemaphoreSlim _candado = new(1, 1);
    private string? _encargoId;

    /// <summary>
    /// Registra, una sola vez por instancia, el encargo de <see cref="TenantConEncargo"/> a favor
    /// del Operador CAE externo de demo, y devuelve su Id. Lo registra la cuenta de plataforma, con
    /// origen <c>AprovisionamientoDePlataforma</c>: el actor nunca es alguien del Operador CAE.
    /// </summary>
    public async Task<string> AsegurarEncargoAsync()
    {
        await _candado.WaitAsync();
        try
        {
            if (_encargoId is not null)
                return _encargoId;

            await SembrarCarterasDelTenantEnteroAsync();

            var id = Guid.CreateVersion7().ToString();
            var filas = await EjecutarSqlAsync(
                """
                INSERT INTO "EncargosAdministracion"
                    ("Id", "PropietarioTenantId", "OperadorTenantId", "AsignacionOperacionId", "ClausulaContrato",
                     "VersionTexto", "Origen", "RegistradoPorUsuarioId", "RegistradoEnUtc", "VigenciaDesde", "Version")
                SELECT @id::uuid, o."PropietarioTenantId", o."OperadorTenantId", o."Id", @clausula,
                       @version, 'AprovisionamientoDePlataforma', u."Id", @ahora::timestamptz, @ahora::timestamptz,
                       @fila::uuid
                FROM "AsignacionesOperacion" o
                JOIN "Tenants" propietario ON propietario."Id" = o."PropietarioTenantId"
                JOIN "Tenants" operador ON operador."Id" = o."OperadorTenantId"
                CROSS JOIN "AspNetUsers" u
                WHERE propietario."Nombre" = @propietario
                  AND operador."Nombre" = @operador
                  AND u."NormalizedEmail" = upper(@actor)
                  AND NOT o."EsRaiz"
                  AND o."OperadorTenantId" <> o."PropietarioTenantId"
                  AND o."Estado" = 'Vigente'
                  AND o."VigenciaHasta" IS NULL
                  AND o."AmbitoRelacionClienteId" IS NULL
                  AND o."AmbitoCentroId" IS NULL
                  AND o."AmbitoTrabajadorId" IS NULL
                  AND o."AmbitoProyectoId" IS NULL
                """,
                ("id", id),
                ("clausula", "Cláusula de prueba E2E, sin valor contractual (siembra de test)"),
                ("version", VersionTextoVigente),
                ("ahora", DateTime.UtcNow.ToString("O")),
                ("fila", Guid.NewGuid().ToString()),
                ("propietario", TenantConEncargo),
                ("operador", Ayudas.NombreTenantConsultora),
                ("actor", Ayudas.EmailAdministrador));
            Assert.Equal(1, filas);

            return _encargoId = id;
        }
        finally
        {
            _candado.Release();
        }
    }

    /// <summary>
    /// Una Asignación de Cartera vigente, de Gestor CAE y de ámbito universal, del Administrador del
    /// Operador CAE externo de demo bajo la operación de cada uno de los dos Tenants propietarios.
    /// Las columnas son las de <c>AsignacionesCartera</c> en la línea base del esquema; los ámbitos
    /// se dejan nulos (universal) y <c>EsPrincipal</c> en su valor por defecto. Tienen que salir
    /// exactamente dos filas: una por Tenant propietario.
    /// </summary>
    private async Task SembrarCarterasDelTenantEnteroAsync()
    {
        var filas = await EjecutarSqlAsync(
            """
            INSERT INTO "AsignacionesCartera"
                ("Id", "AsignacionOperacionId", "UsuarioId", "Rol", "PropietarioTenantId", "OperadorTenantId",
                 "VigenciaDesde", "Estado", "Version", "CreadoEnUtc")
            SELECT gen_random_uuid(), o."Id", u."Id", 'GestorCae', o."PropietarioTenantId", o."OperadorTenantId",
                   @ahora::timestamptz - interval '1 hour', 'Vigente', gen_random_uuid(), @ahora::timestamptz
            FROM "AsignacionesOperacion" o
            JOIN "Tenants" propietario ON propietario."Id" = o."PropietarioTenantId"
            JOIN "Tenants" operador ON operador."Id" = o."OperadorTenantId"
            CROSS JOIN "AspNetUsers" u
            WHERE propietario."Nombre" IN (@conEncargo, @sinEncargo)
              AND operador."Nombre" = @operador
              AND u."NormalizedEmail" = upper(@gestor)
              AND u."TenantId" = o."OperadorTenantId"
              AND NOT o."EsRaiz"
              AND o."OperadorTenantId" <> o."PropietarioTenantId"
              AND o."Estado" = 'Vigente'
              AND o."VigenciaHasta" IS NULL
              AND o."AmbitoRelacionClienteId" IS NULL
              AND o."AmbitoCentroId" IS NULL
              AND o."AmbitoTrabajadorId" IS NULL
              AND o."AmbitoProyectoId" IS NULL
            """,
            ("ahora", DateTime.UtcNow.ToString("O")),
            ("conEncargo", TenantConEncargo),
            ("sinEncargo", TenantSinEncargo),
            ("operador", Ayudas.NombreTenantConsultora),
            ("gestor", Ayudas.EmailAdministradorConsultora));
        Assert.Equal(2, filas);
    }
}
