// Generador de carga real de LibreOffice y Chromium — REC-196. Ver
// CargaLibreOfficeChromium.csproj para el porqué de cada decisión de diseño
// (semáforo real, chromium-headless-shell, aislamiento de CaeManager.slnx).
//
// Medición de memoria en ESTE arnés (a diferencia de CargaCircuitos, que ya
// sabe leer cgroup de un contenedor real): suma el RSS de los procesos hijos
// reales (soffice.bin / chrome_headless_shell) vía `ps`, muestreado durante
// la carga, porque este binario corre hoy fuera de cualquier contenedor
// (validación de instrumento en WSL2, no la Combinación D completa todavía
// — esa exige el modo --attach-url/--container de CargaCircuitos orquestando
// los tres a la vez, pendiente de que el contenedor deje de reiniciarse
// solo). Sin RSS-por-PID solo se tendría el proceso .NET, que no es donde
// vive el coste real: LibreOffice y Chromium son procesos EXTERNOS.
using System.Diagnostics;
using System.Text;
using CaeManager.Infrastructure.Conversion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

var modo = ParseString(args, "--modo", "");
var concurrencia = ParseInt(args, "--concurrencia", 4);
var duracionSegundos = ParseInt(args, "--duracion-segundos", 30);
var docxMb = ParseDouble(args, "--docx-mb", 2.0);
var timeoutSegundos = ParseInt(args, "--timeout-segundos", 60);

if (modo == "instalar-chromium-shell")
{
    // Sin pwsh en el host (Linux, sin PowerShell Core), el playwright.sh que
    // el paquete genera para otras plataformas no sirve aquí — se invoca el
    // instalador directamente, igual que en la sonda REC-196 § 8 que
    // confirmó que el driver acepta SOLO el shell.
    return Microsoft.Playwright.Program.Main(["install", "chromium-headless-shell"]);
}

if (modo == "instalar-dependencias-chromium")
{
    // Un WSL2/Ubuntu desnudo no trae las librerías compartidas que Chromium
    // headless necesita (libasound2 y similares) — el Dockerfile real
    // tampoco las instala hoy porque ADR-007 no está construido. Este modo
    // deja constancia de EXACTAMENTE qué paquetes haría falta añadir al
    // Dockerfile si se aprueba: es información real para el coste de
    // imagen, no una suposición.
    return Microsoft.Playwright.Program.Main(["install-deps", "chromium"]);
}

if (modo is not ("libreoffice" or "chromium"))
{
    Console.Error.WriteLine("Uso: --modo libreoffice|chromium|instalar-chromium-shell [--concurrencia N] [--duracion-segundos S] [--docx-mb X] [--timeout-segundos S]");
    return 1;
}

Console.WriteLine($"Modo: {modo} — concurrencia solicitada: {concurrencia} — duración objetivo: {duracionSegundos}s");

using var muestreoCts = new CancellationTokenSource();
var muestrasRssMb = new List<double>();
var muestreoLock = new object();

// `ps -o comm` trunca a 15 caracteres y usa el nombre real del binario
// (guion, no guion bajo) — comprobado lanzando chrome-headless-shell a mano
// y mirando `ps`: aparece como "chrome-headless", no
// "chrome_headless_shell". Un primer intento con guion bajo daba SIEMPRE
// 0 MB sin ningún error — un cero silencioso que hubiera pasado por "sin
// consumo" en vez de "el filtro no casa con nada" (ver INSTRUMENTOS-Y-SUS-TRAMPAS.md
// en el repositorio de negocio sobre exactamente este tipo de trampa).
string[] nombresProceso = modo == "libreoffice"
    ? ["soffice.bin", "soffice"]
    : ["chrome-headless"];

var tareaMuestreo = MuestrearRssAsync(nombresProceso, muestrasRssMb, muestreoLock, muestreoCts.Token);

var cronometroTotal = Stopwatch.StartNew();

if (modo == "libreoffice")
    await EjecutarCargaLibreOfficeAsync(concurrencia, docxMb, timeoutSegundos);
else
    await EjecutarCargaChromiumAsync(concurrencia, duracionSegundos);

cronometroTotal.Stop();
muestreoCts.Cancel();
try { await tareaMuestreo; } catch (OperationCanceledException) { }

double picoMb, promedioMb;
List<double> serieMb;
lock (muestreoLock)
{
    picoMb = muestrasRssMb.Count > 0 ? muestrasRssMb.Max() : double.NaN;
    promedioMb = muestrasRssMb.Count > 0 ? muestrasRssMb.Average() : double.NaN;
    serieMb = [.. muestrasRssMb];
}

Console.WriteLine();
Console.WriteLine($"=== Resumen ({modo}) ===");
Console.WriteLine($"Duración real: {cronometroTotal.Elapsed.TotalSeconds:F1}s — muestras RSS: {muestrasRssMb.Count}");
Console.WriteLine($"RSS de {string.Join("/", nombresProceso)} — pico: {picoMb:F0} MB, promedio: {promedioMb:F0} MB");
Console.WriteLine($"Serie temporal (1 muestra/s, para distinguir techo estable de fuga): {string.Join(", ", serieMb.Select(m => m.ToString("F0")))}");
Console.WriteLine("AVISO: RSS por `ps`, no cgroup — para la cifra de decisión de REC-196 hace falta el modo --container de CargaCircuitos con estos tres orquestados a la vez dentro del mismo contenedor.");

return 0;

static async Task EjecutarCargaLibreOfficeAsync(int concurrencia, double docxMb, int timeoutSegundos)
{
    var docx = ConstruirDocxRealista(docxMb);
    Console.WriteLine($"Documento sintético construido: {docx.Length / 1024.0 / 1024.0:F2} MB en disco (.docx, comprimido) para un objetivo de {docxMb:F1} MB.");

    var servicio = new LibreOfficeConversorWordPdfService(
        Options.Create(new LibreOfficeConversorWordPdfServiceOptions { TimeoutSegundos = timeoutSegundos }),
        NullLogger<LibreOfficeConversorWordPdfService>.Instance);

    Console.WriteLine($"Lanzando {concurrencia} conversiones \"concurrentes\" contra el servicio real — el SemaphoreSlim(1,1) de producción las va a serializar. Se espera ver la cola, no paralelismo real.");

    var tareas = new List<Task<(int Indice, double EsperaMs, double ConversionMs, int TamanoPdf)>>();
    var inicioGlobal = Stopwatch.StartNew();

    for (var i = 0; i < concurrencia; i++)
    {
        var indice = i;
        tareas.Add(Task.Run(async () =>
        {
            var antesDeEsperar = inicioGlobal.Elapsed.TotalMilliseconds;
            var cronometro = Stopwatch.StartNew();
            var pdf = await servicio.ConvertirAPdfAsync(docx);
            cronometro.Stop();
            return (indice, antesDeEsperar, cronometro.Elapsed.TotalMilliseconds, pdf.Length);
        }));
    }

    var resultados = await Task.WhenAll(tareas);

    foreach (var r in resultados.OrderBy(r => r.Indice))
        Console.WriteLine($"  Conversión #{r.Indice}: lanzada en t={r.EsperaMs:F0}ms, tardó {r.ConversionMs:F0}ms en completar (incluye espera de cola), PDF={r.TamanoPdf / 1024}KB");
}

static async Task EjecutarCargaChromiumAsync(int concurrencia, int duracionSegundos)
{
    var html = ConstruirHtmlRepresentativo();

    using var playwright = await Playwright.CreateAsync();
    // Canal explícito para dejar constancia en el propio log de que se probó
    // SOLO con el shell instalado (REC-196 § 8) — no con el paquete
    // "chromium" completo.
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        Headless = true,
        Channel = "chromium-headless-shell",
    });

    Console.WriteLine($"Chromium (headless-shell) lanzado, versión {browser.Version}. {concurrencia} instancias renderizando en bucle sostenido durante {duracionSegundos}s.");

    using var ctsDuracion = new CancellationTokenSource(TimeSpan.FromSeconds(duracionSegundos));
    var renders = 0;

    var tareas = Enumerable.Range(0, concurrencia).Select(_ => Task.Run(async () =>
    {
        await using var contexto = await browser.NewContextAsync();
        var pagina = await contexto.NewPageAsync();

        while (!ctsDuracion.IsCancellationRequested)
        {
            try
            {
                await pagina.SetContentAsync(html, new PageSetContentOptions { Timeout = 15_000 });
                await pagina.PdfAsync(); // ADR-007 propone render a PDF, no solo pintar HTML.
                Interlocked.Increment(ref renders);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // No es un test de corrección: un timeout aislado bajo
                // memoria/CPU saturados es exactamente la señal de
                // degradación que se está buscando, no un motivo para
                // tirar toda la ventana de medición. Se cuenta como fallo
                // y se sigue — la serie de RSS del sampler es lo que
                // importa conservar entera.
                Console.Error.WriteLine($"  [render fallido, se continúa] {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            }
        }
    })).ToList();

    try { await Task.WhenAll(tareas); } catch (OperationCanceledException) { }

    Console.WriteLine($"Renders completados en la ventana: {renders} (entre {concurrencia} instancias).");
}

/// <summary>
/// .docx con múltiples párrafos de texto NO repetitivo (evita que la
/// compresión del .zip subyacente colapse el tamaño a unos pocos KB, que es
/// lo que le pasaría a un párrafo repetido mil veces) — se acerca más al
/// perfil real de un documento subido que el .docx mínimo de una línea que
/// usan los tests unitarios (ver LibreOfficeConversorWordPdfServiceTests.cs,
/// que documenta por qué ESE mínimo es correcto para su propósito distinto).
/// </summary>
static byte[] ConstruirDocxRealista(double objetivoMb)
{
    var random = new Random(20260905);
    var palabras = new[]
    {
        "cliente", "gestor", "operador", "cartera", "documento", "vigencia", "acreditacion",
        "formato", "tenant", "auditoria", "circuito", "render", "coincidencia", "memoria",
        "postgres", "contenedor", "produccion", "seguridad", "trabajador", "subcontrata",
    };

    var parrafos = new StringBuilder();
    var bytesObjetivo = (long)(objetivoMb * 1024 * 1024 * 3); // ratio de compresión típico de texto ~3:1
    while (parrafos.Length < bytesObjetivo)
    {
        var longitudParrafo = 40 + random.Next(60);
        for (var i = 0; i < longitudParrafo; i++)
            parrafos.Append(palabras[random.Next(palabras.Length)]).Append(' ');
        parrafos.Append('\n');
    }

    using var memoria = new MemoryStream();
    using (var zip = new System.IO.Compression.ZipArchive(memoria, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
    {
        EscribirEntrada(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
            <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
            <Default Extension="xml" ContentType="application/xml"/>
            <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
            </Types>
            """);

        EscribirEntrada(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
            </Relationships>
            """);

        var cuerpo = new StringBuilder();
        foreach (var linea in parrafos.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            cuerpo.Append("<w:p><w:r><w:t xml:space=\"preserve\">").Append(System.Security.SecurityElement.Escape(linea)).Append("</w:t></w:r></w:p>");

        EscribirEntrada(zip, "word/document.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
            <w:body>{cuerpo}</w:body>
            </w:document>
            """);
    }

    return memoria.ToArray();
}

static void EscribirEntrada(System.IO.Compression.ZipArchive zip, string nombre, string contenido)
{
    var entrada = zip.CreateEntry(nombre, System.IO.Compression.CompressionLevel.NoCompression);
    using var escritor = new StreamWriter(entrada.Open(), Encoding.UTF8);
    escritor.Write(contenido);
}

/// <summary>
/// HTML representativo del tipo de formato que ADR-007 propone renderizar:
/// tabla con filas repetidas y CSS con selectores no triviales, para no medir
/// el caso más benigno (un &lt;h1&gt; suelto) que el propio REC-196 § 12(a)
/// pide evitar.
/// </summary>
static string ConstruirHtmlRepresentativo()
{
    var filas = new StringBuilder();
    for (var i = 0; i < 200; i++)
        filas.Append($"<tr><td>{i}</td><td>Trabajador de prueba {i}</td><td>EPI-{i % 12}</td><td>Vigente</td></tr>");

    return $$"""
        <!doctype html>
        <html><head><style>
        body { font-family: sans-serif; font-size: 11px; }
        table { width: 100%; border-collapse: collapse; }
        td, th { border: 1px solid #333; padding: 4px 8px; }
        tr:nth-child(even) { background: #f0f0f0; }
        </style></head>
        <body>
        <h1>REC-196 — plantilla sintética de render</h1>
        <table><thead><tr><th>#</th><th>Trabajador</th><th>EPI</th><th>Estado</th></tr></thead>
        <tbody>{{filas}}</tbody></table>
        </body></html>
        """;
}

static async Task MuestrearRssAsync(string[] nombresProceso, List<double> destino, object candado, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            var (exitCode, salida, _) = await EjecutarProcesoAsync("ps", "-eo rss,comm --no-headers");
            if (exitCode == 0)
            {
                var totalKb = 0L;
                foreach (var linea in salida.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var partes = linea.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (partes.Length != 2) continue;
                    if (!nombresProceso.Any(n => partes[1].Contains(n, StringComparison.OrdinalIgnoreCase))) continue;
                    if (long.TryParse(partes[0], out var rssKb)) totalKb += rssKb;
                }

                lock (candado) destino.Add(totalKb / 1024.0);
            }
        }
        catch { /* mejor esfuerzo — una muestra fallida no debe tumbar el arnés */ }

        try { await Task.Delay(1000, ct); }
        catch (OperationCanceledException) { break; }
    }
}

static async Task<(int ExitCode, string Salida, string Error)> EjecutarProcesoAsync(string archivo, string argumentos)
{
    var info = new ProcessStartInfo
    {
        FileName = archivo,
        Arguments = argumentos,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };

    using var proceso = Process.Start(info) ?? throw new InvalidOperationException($"No se pudo ejecutar \"{archivo} {argumentos}\".");
    var salida = await proceso.StandardOutput.ReadToEndAsync();
    var error = await proceso.StandardError.ReadToEndAsync();
    await proceso.WaitForExitAsync();
    return (proceso.ExitCode, salida, error);
}

static string ParseString(string[] args, string nombre, string porDefecto)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == nombre) return args[i + 1];
    return porDefecto;
}

static int ParseInt(string[] args, string nombre, int porDefecto)
{
    var valor = ParseString(args, nombre, "");
    return string.IsNullOrEmpty(valor) ? porDefecto : int.Parse(valor);
}

static double ParseDouble(string[] args, string nombre, double porDefecto)
{
    var valor = ParseString(args, nombre, "");
    return string.IsNullOrEmpty(valor) ? porDefecto : double.Parse(valor, System.Globalization.CultureInfo.InvariantCulture);
}
