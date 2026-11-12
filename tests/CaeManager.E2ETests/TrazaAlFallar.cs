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
/// guardar, el motivo queda en un .txt con el mismo nombre en ese directorio y en la salida de error, sin tapar el
/// fallo del test.
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
        var ruta = traza.RutaNueva($"tramo{traza._tramosAnteriores.Count + 1}");
        await contexto.Tracing.StopChunkAsync(new TracingStopChunkOptions { Path = ruta });
        traza._tramosAnteriores.Add(ruta);
        traza._grabando = false;
    }

    /// <summary>Al acabar un inicio de sesión: la grabación empieza (o sigue, en un tramo nuevo) a partir de aquí.</summary>
    internal static async Task ReanudarTrasIniciarSesionAsync(IBrowserContext contexto)
    {
        if (!Armadas.TryGetValue(contexto, out var traza) || traza._grabando) return;
        if (traza._iniciada)
            await contexto.Tracing.StartChunkAsync(new TracingStartChunkOptions { Title = traza._nombre });
        else
            await contexto.Tracing.StartAsync(new TracingStartOptions { Title = traza._nombre, Screenshots = true, Snapshots = true });
        traza._iniciada = true;
        traza._grabando = true;
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
        if (!_iniciada) return;

        var ruta = _superada || !_grabando ? null : Path.Combine(Directorio(), $"{_nombre}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
        try
        {
            if (ruta is not null) Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
            // Sin ruta, Playwright descarta el tramo en curso (o no hay ninguno, si se cortó para otro inicio de sesión).
            await _contexto.Tracing.StopAsync(new TracingStopOptions { Path = ruta });
            if (_superada)
                foreach (var tramo in _tramosAnteriores) File.Delete(tramo);
            else
                foreach (var guardada in _tramosAnteriores.Append(ruta).OfType<string>())
                    Console.WriteLine($"Traza de Playwright del test fallido: {guardada}");
        }
        catch (Exception excepcion) when (excepcion is PlaywrightException or IOException or UnauthorizedAccessException)
        {
            // El contexto o el navegador ya se cerraron (p. ej. el test falló porque se cayó el navegador) o no se pudo
            // escribir el fichero: tapar con esto la excepción original del test sería peor que perder la traza, pero sin
            // dejar rastro un artefacto sin traza no se distinguiría de un test que no la armó.
            if (!_superada) AnotarQueNoSeGuardo(ruta, excepcion);
        }
    }

    private void AnotarQueNoSeGuardo(string? ruta, Exception excepcion)
    {
        var mensaje = $"No se pudo guardar la traza de Playwright de {_nombre} ({ruta ?? "sin ruta"}): {excepcion.GetType().Name}: {excepcion.Message}";
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
