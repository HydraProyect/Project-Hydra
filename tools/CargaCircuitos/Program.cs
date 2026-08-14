// Harness de carga de circuitos de Blazor Server — Horizonte 2.1.
//
// Arranca CaeManager.Web como proceso real (mismo binario de despliegue)
// contra una base de datos PostgreSQL temporal sembrada con datos de
// prueba, y abre circuitos reales con Playwright (login real, sesión
// interactiva real) en tandas crecientes, midiendo memoria del proceso
// servidor y latencia de una interacción representativa (paginar /clientes,
// que pasa por PuertaAccesoDatos) en cada tanda. Ver CargaCircuitos.csproj
// para por qué este harness vive fuera de CaeManager.slnx y de
// tests/CaeManager.E2ETests.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using Npgsql;

var stages = ParseIntList(args, "--stages", [5, 10, 20, 30, 45, 60, 80, 100, 130, 160]);
var holdSeconds = ParseInt(args, "--hold", 25);
var ruta = ParseString(args, "--route", "/clientes");
var cpus = ParseInt(args, "--cpus", 2);
var memBudgetMb = ParseInt(args, "--mem-budget-mb", 2800);
var latencyBudgetMs = ParseInt(args, "--latency-budget-ms", 3000);
var outDir = ParseString(
    args,
    "--out",
    Path.Combine(Path.GetTempPath(), "carga-circuitos-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)));

Directory.CreateDirectory(outDir);

Console.WriteLine($"Etapas objetivo: {string.Join(", ", stages)}");
Console.WriteLine($"Espera por etapa: {holdSeconds}s — ruta ejercitada: {ruta}");
Console.WriteLine($"Constricción de CPU del proceso servidor (DOTNET_PROCESSOR_COUNT): {cpus}");
Console.WriteLine($"Presupuesto de memoria (RAM disponible real del VPS de producción): {memBudgetMb} MB — presupuesto de latencia p95: {latencyBudgetMs} ms");
Console.WriteLine($"Salida: {outDir}");
Console.WriteLine();

var servidorPg = Environment.GetEnvironmentVariable("CAEMANAGER_TESTS_PG") ?? "Host=localhost;Username=postgres;Password=postgres";
var nombreBd = $"caemanager_carga_{Guid.NewGuid():N}";
var cadenaConexion = $"{servidorPg};Database={nombreBd}";

var rutaDll = LocalizarCaeManagerWebDll();
var puerto = ObtenerPuertoLibre();
var baseUrl = $"http://127.0.0.1:{puerto}";

Console.WriteLine($"Arrancando {rutaDll} en {baseUrl} contra {nombreBd}...");

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
infoInicio.Environment["ASPNETCORE_URLS"] = baseUrl;
infoInicio.Environment["ConnectionStrings__CaeManagerDb"] = cadenaConexion;
infoInicio.Environment["DatosPrueba__Activo"] = "true";

// Mismo motivo que WebAppFixture.cs: decenas de logins reales en poco
// tiempo desde 127.0.0.1 chocarían con el límite de fuerza bruta de
// /cuenta/* (P0-2). Aquí el "ataque" es el propio harness.
infoInicio.Environment["RateLimiting__Cuenta__LimiteAnonimo"] = "5000";
infoInicio.Environment["RateLimiting__Cuenta__LimiteAutenticado"] = "5000";

// Aproximación sin Docker/cgroups al presupuesto de 2 vCPU del VPS de
// producción (ver ADR-008): DOTNET_PROCESSOR_COUNT limita cuántos
// procesadores ve el runtime (thread pool, Server GC con tantos heaps como
// procesadores) sin necesitar un contenedor. La memoria se deja SIN
// constreñir a propósito — lo que interesa medir es el crecimiento
// orgánico de RAM por circuito para extrapolarlo contra el presupuesto real
// (3.7 GB totales / ~2.8 GB disponibles), no forzar un techo artificial de
// heap que podría producir un patrón de GC distinto al de producción.
infoInicio.Environment["DOTNET_PROCESSOR_COUNT"] = cpus.ToString(CultureInfo.InvariantCulture);

var proceso = Process.Start(infoInicio) ?? throw new InvalidOperationException("No se pudo arrancar CaeManager.Web.");

proceso.OutputDataReceived += (_, _) => { };
proceso.ErrorDataReceived += (_, e) =>
{
    if (!string.IsNullOrEmpty(e.Data))
        Console.Error.WriteLine($"[CaeManager.Web] {e.Data}");
};
proceso.BeginOutputReadLine();
proceso.BeginErrorReadLine();

try
{
    await EsperarArranqueAsync(baseUrl, proceso);
    Console.WriteLine("Servidor arriba, migraciones y siembra completas. Arrancando Playwright...");

    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

    var usuarios = ConstruirUsuariosPrueba();
    var contextos = new List<(IBrowserContext Contexto, IPage Pagina)>();
    var muestras = new List<MuestraMemoria>();
    var muestrasLock = new object();
    using var muestreoCts = new CancellationTokenSource();

    var tareaMuestreo = MuestrearMemoriaAsync(proceso.Id, () => contextos.Count, cpus, muestras, muestrasLock, muestreoCts.Token);

    var resumenEtapas = new List<ResumenEtapa>();
    var techoAlcanzado = false;
    string? razonTecho = null;

    foreach (var n in stages)
    {
        Console.WriteLine();
        Console.WriteLine($"=== Etapa N={n} ===");

        var erroresApertura = 0;
        while (contextos.Count < n)
        {
            try
            {
                var contexto = await browser.NewContextAsync();
                var pagina = await contexto.NewPageAsync();
                var usuario = usuarios[contextos.Count % usuarios.Count];

                await IniciarSesionAsync(pagina, baseUrl, usuario.Email, usuario.Password);
                await pagina.GotoAsync(baseUrl + ruta);
                await pagina.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 20_000 });

                contextos.Add((contexto, pagina));
            }
            catch (Exception ex)
            {
                erroresApertura++;
                Console.Error.WriteLine($"  Error abriendo circuito #{contextos.Count + 1}: {ex.Message}");
                if (erroresApertura > 5)
                {
                    Console.Error.WriteLine("  Demasiados errores abriendo circuitos seguidos — se corta la etapa aquí.");
                    break;
                }
            }
        }

        Console.WriteLine($"  {contextos.Count} circuitos abiertos (objetivo {n}, {erroresApertura} errores de apertura). Manteniendo {holdSeconds}s con interacción...");

        // Un bucle de interacción POR CIRCUITO (no un único bucle rotando por
        // turnos): con un solo bucle el ritmo total de peticiones queda fijo
        // (una interacción cada ~120ms en total, sin importar N) y la carga
        // real sobre el servidor NO crece con el número de circuitos — el
        // primer diseño de este harness tenía justo ese defecto (CPU medida
        // cayendo de ~26% a ~3% al subir N, prueba de que el ritmo total no
        // escalaba). Con un bucle por circuito, cada usuario interactúa a un
        // ritmo aproximadamente constante (~1 interacción cada 1.5-2.5s, con
        // jitter para no sincronizarlos en el mismo tick) y el ritmo TOTAL de
        // peticiones crece con N — la única forma de que esto sea de verdad
        // una prueba de carga y no solo una prueba de "N circuitos abiertos
        // sin usar".
        var latenciasConcurrentes = new System.Collections.Concurrent.ConcurrentBag<double>();
        var erroresInteraccion = 0;
        var interacciones = 0;
        var inicioHold = DateTime.UtcNow;

        using var ctsHold = new CancellationTokenSource(TimeSpan.FromSeconds(holdSeconds));

        var tareasInteraccion = contextos.Select(circuito => Task.Run(async () =>
        {
            try { await Task.Delay(Random.Shared.Next(0, 1500), ctsHold.Token); }
            catch (OperationCanceledException) { return; }

            while (!ctsHold.IsCancellationRequested)
            {
                Interlocked.Increment(ref interacciones);
                try
                {
                    var latenciaMs = await InteractuarAsync(circuito.Pagina);
                    if (latenciaMs is not null)
                        latenciasConcurrentes.Add(latenciaMs.Value);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref erroresInteraccion);
                    Console.Error.WriteLine($"  Error en interacción: {ex.Message}");
                }

                try { await Task.Delay(1500 + Random.Shared.Next(0, 1000), ctsHold.Token); }
                catch (OperationCanceledException) { break; }
            }
        })).ToList();

        await Task.WhenAll(tareasInteraccion);

        var latencias = latenciasConcurrentes.ToList();

        List<MuestraMemoria> muestrasEtapa;
        lock (muestrasLock)
        {
            muestrasEtapa = muestras.Where(m => m.Hora >= inicioHold && m.Hora <= DateTime.UtcNow).ToList();
        }

        var memoriaPromedioMb = muestrasEtapa.Count > 0 ? muestrasEtapa.Average(m => m.WorkingSetMb) : double.NaN;
        var memoriaMaximaMb = muestrasEtapa.Count > 0 ? muestrasEtapa.Max(m => m.WorkingSetMb) : double.NaN;
        var cpuPromedioPct = muestrasEtapa.Count > 0 ? muestrasEtapa.Average(m => m.CpuPercentDelBudget) : double.NaN;

        latencias.Sort();
        var p50 = Percentil(latencias, 0.50);
        var p95 = Percentil(latencias, 0.95);
        var max = latencias.Count > 0 ? latencias[^1] : double.NaN;
        var tasaError = interacciones > 0 ? (double)erroresInteraccion / interacciones : 0;

        var resumen = new ResumenEtapa(
            n, contextos.Count, erroresApertura, memoriaPromedioMb, memoriaMaximaMb, cpuPromedioPct,
            latencias.Count, p50, p95, max, erroresInteraccion, tasaError);
        resumenEtapas.Add(resumen);

        Console.WriteLine($"  Memoria (working set) promedio={memoriaPromedioMb:F0} MB, máxima={memoriaMaximaMb:F0} MB — CPU promedio ~{cpuPromedioPct:F0}% del presupuesto de {cpus} vCPU");
        Console.WriteLine($"  Latencia paginar {ruta}: p50={p50:F0} ms, p95={p95:F0} ms, max={max:F0} ms sobre {latencias.Count} muestras ({erroresInteraccion} errores, {tasaError:P1})");

        if (memoriaMaximaMb > memBudgetMb)
        {
            techoAlcanzado = true;
            razonTecho = $"memoria máxima ({memoriaMaximaMb:F0} MB) superó el presupuesto de producción ({memBudgetMb} MB)";
        }
        else if (!double.IsNaN(p95) && p95 > latencyBudgetMs)
        {
            techoAlcanzado = true;
            razonTecho = $"latencia p95 ({p95:F0} ms) superó el presupuesto ({latencyBudgetMs} ms)";
        }
        else if (tasaError > 0.10)
        {
            techoAlcanzado = true;
            razonTecho = $"tasa de error de interacción ({tasaError:P1}) superó el 10%";
        }
        else if (contextos.Count < n)
        {
            techoAlcanzado = true;
            razonTecho = $"no se pudieron abrir los {n} circuitos objetivo (se quedó en {contextos.Count} tras {erroresApertura} errores)";
        }

        if (techoAlcanzado)
        {
            Console.WriteLine($"  >>> TECHO ALCANZADO en esta etapa: {razonTecho}");
            break;
        }
    }

    muestreoCts.Cancel();
    try { await tareaMuestreo; } catch (OperationCanceledException) { }

    Console.WriteLine();
    Console.WriteLine("=== Resumen ===");
    Console.WriteLine("N_objetivo\tN_real\tMemMediaMB\tMemMaxMB\tCPU%\tLatP50ms\tLatP95ms\tErrores%");
    foreach (var r in resumenEtapas)
    {
        Console.WriteLine($"{r.NObjetivo}\t{r.NReal}\t{r.MemoriaPromedioMb:F0}\t{r.MemoriaMaximaMb:F0}\t{r.CpuPromedioPct:F0}\t{r.P50Ms:F0}\t{r.P95Ms:F0}\t{r.TasaError:P1}");
    }

    if (techoAlcanzado)
        Console.WriteLine($"Techo alcanzado: {razonTecho}");
    else
        Console.WriteLine("No se alcanzó ningún techo dentro de las etapas configuradas — considerar ampliar --stages.");

    var rutaResumenJson = Path.Combine(outDir, "resumen-etapas.json");
    await File.WriteAllTextAsync(rutaResumenJson, JsonSerializer.Serialize(new
    {
        stages,
        holdSeconds,
        ruta,
        cpus,
        memBudgetMb,
        latencyBudgetMs,
        resumenEtapas,
        techoAlcanzado,
        razonTecho
    }, new JsonSerializerOptions
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    }));

    var rutaMuestrasCsv = Path.Combine(outDir, "muestras-memoria.csv");
    await using (var escritor = new StreamWriter(rutaMuestrasCsv))
    {
        await escritor.WriteLineAsync("hora_utc,circuitos,working_set_mb,cpu_pct_del_presupuesto");
        List<MuestraMemoria> copia;
        lock (muestrasLock) copia = [.. muestras];
        foreach (var m in copia)
            await escritor.WriteLineAsync($"{m.Hora:O},{m.Circuitos},{m.WorkingSetMb:F1},{m.CpuPercentDelBudget:F1}");
    }

    Console.WriteLine($"Resumen JSON: {rutaResumenJson}");
    Console.WriteLine($"Muestras CSV: {rutaMuestrasCsv}");

    foreach (var (contexto, _) in contextos)
        await contexto.CloseAsync();
}
finally
{
    if (proceso is { HasExited: false })
    {
        try
        {
            proceso.Kill(entireProcessTree: true);
            await proceso.WaitForExitAsync();
        }
        catch (InvalidOperationException) { }
    }

    proceso.Dispose();

    try
    {
        await EliminarBaseDatosAsync(servidorPg, nombreBd);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"No se pudo borrar la base de datos temporal {nombreBd}: {ex.Message}");
    }
}

return;

static async Task<double?> InteractuarAsync(IPage pagina)
{
    // Algún componente del design system (Modal.razor) puede quedar abierto
    // en pantalla por interacción previa (p. ej. un modal no bloqueante
    // disparado por el propio render de la página) y su
    // ".modal-superposicion" intercepta los clics del paginador — no es un
    // límite de capacidad del servidor, es ruido del harness. Escape cierra
    // cualquier modal no bloqueante (ver Modal.razor.ManejarTeclaAsync); si
    // el modal es bloqueante, Escape no hace nada y la interacción de este
    // ciclo simplemente se salta (boton seguirá sin encontrarse, se
    // reintentará en el siguiente ciclo).
    if (await pagina.Locator(".modal-superposicion").CountAsync() > 0)
    {
        await pagina.Keyboard.PressAsync("Escape");
        await pagina.WaitForTimeoutAsync(200);

        // Seguía ahí tras Escape: modal bloqueante (Modal.razor no le da
        // salida por teclado a propósito). No es un error del harness ni
        // del servidor — se salta este ciclo de interacción sin contarlo
        // como fallo, se reintentará en el siguiente.
        if (await pagina.Locator(".modal-superposicion").CountAsync() > 0)
            return null;
    }

    var contenedor = pagina.Locator(".paginador-simple");
    if (await contenedor.CountAsync() == 0)
    {
        if (Environment.GetEnvironmentVariable("CARGA_DEBUG") == "1")
            Console.Error.WriteLine($"  [debug] sin .paginador-simple en {pagina.Url} — título: {await pagina.TitleAsync()}");
        return null;
    }

    var siguiente = contenedor.Locator("button", new LocatorLocatorOptions { HasText = "Siguiente" });
    var anterior = contenedor.Locator("button", new LocatorLocatorOptions { HasText = "Anterior" });

    ILocator? boton = null;
    if (await siguiente.CountAsync() > 0 && await siguiente.IsEnabledAsync())
        boton = siguiente;
    else if (await anterior.CountAsync() > 0 && await anterior.IsEnabledAsync())
        boton = anterior;

    if (boton is null)
    {
        if (Environment.GetEnvironmentVariable("CARGA_DEBUG") == "1")
            Console.Error.WriteLine($"  [debug] .paginador-simple presente pero ni Siguiente ni Anterior habilitados en {pagina.Url}");
        return null;
    }

    var textoAntes = await pagina.Locator(".paginador-texto").TextContentAsync() ?? string.Empty;

    var cronometro = Stopwatch.StartNew();
    await boton.ClickAsync(new LocatorClickOptions { Timeout = 15_000 });
    await pagina.WaitForFunctionAsync(
        "([sel, anterior]) => document.querySelector(sel)?.textContent !== anterior",
        new object?[] { ".paginador-texto", textoAntes },
        new PageWaitForFunctionOptions { Timeout = 15_000 });
    cronometro.Stop();

    return cronometro.Elapsed.TotalMilliseconds;
}

static async Task IniciarSesionAsync(IPage pagina, string baseUrl, string email, string password)
{
    await pagina.GotoAsync($"{baseUrl}/cuenta/iniciar-sesion");
    await pagina.FillAsync("#email", email);
    await pagina.FillAsync("#password", password);
    await pagina.ClickAsync("button[type=\"submit\"]");
    await pagina.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 20_000 });
    await pagina.Locator(".nav-principal").WaitForAsync(new LocatorWaitForOptions { Timeout = 20_000 });
}

/// <summary>
/// Las 18 cuentas de prueba sembradas por DatosPruebaSeeder (3 por rol, ver
/// Roles.Todos) — mismo criterio que Ayudas.EmailPrueba pero duplicado aquí
/// (ver CargaCircuitos.csproj sobre por qué este harness no referencia
/// tests/CaeManager.E2ETests). Sin 2FA (a diferencia del Administrador
/// inicial), así que sirven para logins concurrentes en tandas sin tener que
/// calcular TOTP. No hay restricción de sesión única en la app — reutilizar
/// la misma cuenta en varias pestañas (cuando N supera 18) es válido, cada
/// una es su propia cookie/circuito independiente, igual que un usuario real
/// con varias pestañas abiertas.
/// </summary>
static List<(string Email, string Password)> ConstruirUsuariosPrueba()
{
    // "administrador" queda fuera a propósito: P1-13 exige 2FA para TODO
    // usuario en rol Administrador (MainLayout.razor.cs redirige a
    // /cuenta/configurar-2fa si no lo tiene activo), y las cuentas
    // prueba.administrador{n} nacen sin 2FA configurado — igual que
    // Ayudas.IniciarSesionAsync solo sabe calcular el TOTP fijo del
    // Administrador inicial (admin@caemanager.local), no el de estas.
    string[] roles = ["direccioncae", "coordinadorcae", "gestorcae", "consulta", "cliente"];
    var lista = new List<(string, string)>();
    foreach (var rol in roles)
        for (var i = 1; i <= 3; i++)
            lista.Add(($"prueba.{rol}{i}@caemanager.local", "Prueba#2026"));
    return lista;
}

static async Task MuestrearMemoriaAsync(int pid, Func<int> circuitosActuales, int cpuBudget, List<MuestraMemoria> destino, object candado, CancellationToken ct)
{
    Process? proceso = null;
    var cpuAnterior = TimeSpan.Zero;
    var horaAnterior = DateTime.UtcNow;

    while (!ct.IsCancellationRequested)
    {
        try
        {
            proceso ??= Process.GetProcessById(pid);
            proceso.Refresh();

            var workingSetMb = proceso.WorkingSet64 / 1024.0 / 1024.0;
            var cpuActual = proceso.TotalProcessorTime;
            var ahora = DateTime.UtcNow;
            var deltaWall = (ahora - horaAnterior).TotalSeconds;

            var cpuPct = 0.0;
            if (deltaWall > 0.1)
                cpuPct = (cpuActual - cpuAnterior).TotalSeconds / deltaWall / cpuBudget * 100.0;

            cpuAnterior = cpuActual;
            horaAnterior = ahora;

            lock (candado)
                destino.Add(new MuestraMemoria(ahora, circuitosActuales(), workingSetMb, cpuPct));
        }
        catch (ArgumentException)
        {
            break; // el proceso ya no existe
        }
        catch (InvalidOperationException)
        {
            break;
        }

        try
        {
            await Task.Delay(2000, ct);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
}

static double Percentil(List<double> valoresOrdenados, double p)
{
    if (valoresOrdenados.Count == 0) return double.NaN;
    var indice = (int)Math.Ceiling(p * valoresOrdenados.Count) - 1;
    indice = Math.Clamp(indice, 0, valoresOrdenados.Count - 1);
    return valoresOrdenados[indice];
}

static async Task EsperarArranqueAsync(string baseUrl, Process proceso)
{
    using var cliente = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    var limite = DateTime.UtcNow.AddSeconds(90);

    while (DateTime.UtcNow < limite)
    {
        if (proceso.HasExited)
            throw new InvalidOperationException($"CaeManager.Web terminó inesperadamente (código {proceso.ExitCode}) mientras arrancaba.");

        try
        {
            var respuesta = await cliente.GetAsync($"{baseUrl}/salud");
            if (respuesta.IsSuccessStatusCode)
                return;
        }
        catch (HttpRequestException) { }

        await Task.Delay(250);
    }

    throw new TimeoutException($"CaeManager.Web no respondió 200 en {baseUrl}/salud a tiempo.");
}

static int ObtenerPuertoLibre()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var puerto = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return puerto;
}

static string LocalizarCaeManagerWebDll()
{
    var directorio = new DirectoryInfo(AppContext.BaseDirectory);
    while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
        directorio = directorio.Parent;

    if (directorio is null)
        throw new InvalidOperationException($"No se encontró CaeManager.slnx subiendo desde {AppContext.BaseDirectory}.");

#if DEBUG
    const string configuracion = "Debug";
#else
    const string configuracion = "Release";
#endif

    var rutaDll = Path.Combine(directorio.FullName, "src", "CaeManager.Web", "bin", configuracion, "net10.0", "CaeManager.Web.dll");
    if (!File.Exists(rutaDll))
        throw new FileNotFoundException($"No se encontró {rutaDll} — compila CaeManager.slnx primero.", rutaDll);

    return rutaDll;
}

static async Task EliminarBaseDatosAsync(string servidorPg, string nombreBd)
{
    var constructor = new NpgsqlConnectionStringBuilder(servidorPg) { Database = "postgres" };
    await using var conexion = new NpgsqlConnection(constructor.ConnectionString);
    await conexion.OpenAsync();
    await using var comando = conexion.CreateCommand();
    comando.CommandText = $"DROP DATABASE IF EXISTS \"{nombreBd}\" WITH (FORCE);";
    await comando.ExecuteNonQueryAsync();
}

static List<int> ParseIntList(string[] args, string nombre, List<int> porDefecto)
{
    var valor = BuscarArg(args, nombre);
    if (valor is null) return porDefecto;
    return [.. valor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse)];
}

static int ParseInt(string[] args, string nombre, int porDefecto)
{
    var valor = BuscarArg(args, nombre);
    return valor is null ? porDefecto : int.Parse(valor, CultureInfo.InvariantCulture);
}

static string ParseString(string[] args, string nombre, string porDefecto) =>
    BuscarArg(args, nombre) ?? porDefecto;

static string? BuscarArg(string[] args, string nombre)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == nombre)
            return args[i + 1];
    return null;
}

internal readonly record struct MuestraMemoria(DateTime Hora, int Circuitos, double WorkingSetMb, double CpuPercentDelBudget);

internal readonly record struct ResumenEtapa(
    int NObjetivo,
    int NReal,
    int ErroresApertura,
    double MemoriaPromedioMb,
    double MemoriaMaximaMb,
    double CpuPromedioPct,
    int MuestrasLatencia,
    double P50Ms,
    double P95Ms,
    double MaxMs,
    int ErroresInteraccion,
    double TasaError);
