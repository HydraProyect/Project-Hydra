using System.Runtime.CompilerServices;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Traza de Playwright (capturas, instantáneas del DOM, red y acciones) que solo se conserva si el test falla (#2194).
/// Se arma justo después del contexto, para que se libere antes que él, y el test llama a <see cref="Superada"/> en
/// su última línea:
/// <code>
/// await using var contexto = await fixture.Browser.NewContextAsync();
/// await using var traza = TrazaAlFallar.Armar(contexto);
/// ...
/// traza.Superada();
/// </code>
/// <para>
/// No graba el inicio de sesión: armarla no empieza a grabar; empieza al final de <see cref="Ayudas.IniciarSesionAsync"/>,
/// que además corta la grabación mientras dura otro inicio de sesión en el mismo contexto. Así la traza no lleva la
/// contraseña ni el código del segundo factor tecleados, ni el formulario de acceso. Son credenciales de prueba
/// (constantes de <see cref="Ayudas"/>, públicas en este repositorio) contra una instancia de la aplicación que el E2E
/// levanta en 127.0.0.1 con su propia base de datos de prueba, pero ni así deben acabar en un artefacto. Lo que sí
/// lleva la red grabada son la cookie de sesión y los tokens de antifalsificación de esa instancia: solo valen contra
/// ella, y desaparece al acabar el job.
/// </para>
/// <para>
/// Si el test no llega a <see cref="Superada"/> (una aserción o una excepción lo cortan), la traza se escribe en
/// tests/CaeManager.E2ETests/TestResults/trazas-playwright/, que el paso «Publicar logs de CaeManager.Web si el E2E
/// falla» de ci.yml ya sube como artefacto (sube TestResults entero). Se abre con
/// <c>pwsh bin/Debug/net10.0/playwright.ps1 show-trace &lt;zip&gt;</c> o en trace.playwright.dev. Si no se puede
/// guardar, o el test falló antes de que empezara a grabar (en el inicio de sesión), el motivo queda en un
/// <c>…-sin-traza.txt</c> con el mismo nombre en ese directorio y en la salida de error. Un fallo de la traza nunca hace
/// fallar el test ni tapa su excepción.
/// </para>
/// <para>
/// Se arma en los tests que rellenan fechas con <see cref="Ayudas.RellenarFechaAsync"/>, los de la carrera de #2194, y
/// no en toda la colección: grabar capturas e instantáneas en cada acción de cada test alarga el E2E entero para
/// diagnosticar fallos que no son de esa clase.
/// </para>
/// </summary>
public sealed class TrazaAlFallar : IAsyncDisposable
{
    private static readonly ConditionalWeakTable<IBrowserContext, TrazaAlFallar> Armadas = new();

    private readonly IBrowserContext _contexto;
    private readonly string _nombre;
    private readonly List<string> _tramosAnteriores = [];
    private bool _iniciada;
    private bool _grabando;
    private bool _superada;
    private string? _averiada;

    private TrazaAlFallar(IBrowserContext contexto, string nombre)
    {
        _contexto = contexto;
        _nombre = nombre;
    }

    public static TrazaAlFallar Armar(
        IBrowserContext contexto, [CallerMemberName] string prueba = "", [CallerFilePath] string archivo = "")
    {
        var traza = new TrazaAlFallar(contexto, $"{Path.GetFileNameWithoutExtension(archivo)}.{prueba}");
        Armadas.AddOrUpdate(contexto, traza);
        return traza;
    }

    /// <summary>El test llegó al final: la traza se descarta.</summary>
    public void Superada() => _superada = true;

    /// <summary>
    /// Al empezar un inicio de sesión: si el contexto ya estaba grabando, el tramo grabado hasta aquí se guarda aparte
    /// (se borra si el test acaba superado) y se deja de grabar hasta <see cref="ReanudarTrasIniciarSesionAsync"/>.
    /// </summary>
    internal static async Task PausarParaIniciarSesionAsync(IBrowserContext contexto)
    {
        if (!Armadas.TryGetValue(contexto, out var traza) || !traza._grabando) return;
        // Deja de grabar aunque no se pueda guardar el tramo: lo que no puede pasar es que se grabe el acceso.
        traza._grabando = false;
        string? ruta = null;
        try
        {
            ruta = traza.RutaNueva($"tramo{traza._tramosAnteriores.Count + 1}");
            await contexto.Tracing.StopChunkAsync(new TracingStopChunkOptions { Path = ruta });
            traza._tramosAnteriores.Add(ruta);
        }
        catch (Exception excepcion) when (excepcion is PlaywrightException or IOException or UnauthorizedAccessException)
        {
            // La traza es para diagnosticar: un fallo suyo no debe hacer fallar el inicio de sesión del test. Sin saber
            // si el tramo se cortó, no se vuelve a grabar en este contexto.
            traza._averiada = $"no se pudo cortar el tramo antes de otro inicio de sesión ({ruta ?? "sin ruta"}): {excepcion.GetType().Name}: {excepcion.Message}";
        }
    }

    /// <summary>Al acabar un inicio de sesión: la grabación empieza (o sigue, en un tramo nuevo) a partir de aquí.</summary>
    internal static async Task ReanudarTrasIniciarSesionAsync(IBrowserContext contexto)
    {
        if (!Armadas.TryGetValue(contexto, out var traza) || traza._grabando || traza._averiada is not null) return;
        try
        {
            if (traza._iniciada)
                await contexto.Tracing.StartChunkAsync(new TracingStartChunkOptions { Title = traza._nombre });
            else
                await contexto.Tracing.StartAsync(new TracingStartOptions { Title = traza._nombre, Screenshots = true, Snapshots = true });
            traza._iniciada = true;
            traza._grabando = true;
        }
        catch (PlaywrightException excepcion)
        {
            traza._averiada = $"no se pudo empezar a grabar tras el inicio de sesión: {excepcion.Message}";
        }
    }

    /// <summary>Ruta en la que se escribe la traza de un test fallido.</summary>
    public static string Directorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir is null
            ? Path.Combine(AppContext.BaseDirectory, "TestResults", "trazas-playwright")
            : Path.Combine(dir, "tests", "CaeManager.E2ETests", "TestResults", "trazas-playwright");
    }

    private string RutaNueva(string sufijo)
    {
        var directorio = Directorio();
        Directory.CreateDirectory(directorio);
        return Path.Combine(directorio, $"{_nombre}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{sufijo}.zip");
    }

    public async ValueTask DisposeAsync()
    {
        Armadas.Remove(_contexto);
        try
        {
            await DetenerAsync();
        }
        finally
        {
            // Los tramos de un test superado no deben acabar en el artefacto de otro test que falle después.
            if (_superada)
                foreach (var tramo in _tramosAnteriores)
                {
                    try { File.Delete(tramo); }
                    catch (Exception excepcion) when (excepcion is IOException or UnauthorizedAccessException) { }
                }
        }
    }

    private async Task DetenerAsync()
    {
        if (!_iniciada)
        {
            // Sin esto, un test que falla en el inicio de sesión (antes de que empiece a grabar) dejaría un artefacto sin
            // traza y sin explicación.
            if (!_superada)
                AnotarSinTraza(_averiada ?? "el test falló antes de acabar el inicio de sesión, y la traza no graba hasta entonces");
            return;
        }

        var ruta = _superada || !_grabando ? null : Path.Combine(Directorio(), $"{_nombre}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
        try
        {
            if (ruta is not null) Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
            // Sin ruta, Playwright descarta el tramo en curso (o no hay ninguno, si se cortó para otro inicio de sesión).
            await _contexto.Tracing.StopAsync(new TracingStopOptions { Path = ruta });
            if (_superada) return;
            foreach (var guardada in _tramosAnteriores.Append(ruta).OfType<string>())
                Console.WriteLine($"Traza de Playwright del test fallido: {guardada}");
            if (_averiada is not null) AnotarSinTraza(_averiada);
        }
        catch (Exception excepcion) when (excepcion is PlaywrightException or IOException or UnauthorizedAccessException)
        {
            // El contexto o el navegador ya se cerraron (p. ej. el test falló porque se cayó el navegador) o no se pudo
            // escribir el fichero: tapar con esto la excepción original del test sería peor que perder la traza, pero sin
            // dejar rastro un artefacto sin traza no se distinguiría de un test que no la armó.
            if (!_superada)
                AnotarSinTraza($"no se pudo guardar ({ruta ?? "sin ruta"}): {excepcion.GetType().Name}: {excepcion.Message}");
        }
    }

    private void AnotarSinTraza(string motivo)
    {
        var mensaje = $"Traza de Playwright de {_nombre} incompleta o ausente: {motivo}";
        Console.Error.WriteLine(mensaje);
        try
        {
            var directorio = Directorio();
            Directory.CreateDirectory(directorio);
            File.WriteAllText(Path.Combine(directorio, $"{_nombre}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-sin-traza.txt"), mensaje + Environment.NewLine);
        }
        catch (Exception excepcionAlAnotar) when (excepcionAlAnotar is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Tampoco se pudo anotar en {Directorio()}: {excepcionAlAnotar.Message}");
        }
    }
}
