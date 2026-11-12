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
/// (constantes de <see cref="Ayudas"/>, públicas en este repositorio), pero ni así deben acabar en un artefacto, que en
/// este repositorio público puede descargar cualquier usuario de GitHub.
/// </para>
/// <para>
/// Lo que sí lleva la red grabada (Playwright no deja grabar la traza sin ella) son la cookie de sesión, los tokens de
/// antifalsificación y lo que la aplicación protege con Data Protection. Solo valen contra la instancia que levanta
/// <see cref="WebAppFixture"/>: escucha en 127.0.0.1 del runner (ASPNETCORE_URLS), con su propia base de pruebas, y su
/// llavero de Data Protection lo genera ella misma al arrancar en App_Data/dataprotection-keys bajo su content root
/// (RegistroDataProtection, sin DataProtection:RutaClaves en la configuración de E2E). Ese llavero no viaja en la
/// compilación compartida (el job que la empaqueta solo compila), no se publica (el paso de logs sube App_Data/logs, no
/// el llavero) y desaparece con el runner al acabar el job: no hay una clave de la que dependa ninguna otra ejecución.
/// </para>
/// <para>
/// Si el test no llega a <see cref="Superada"/> (una aserción o una excepción lo cortan), los tramos pasan a
/// tests/CaeManager.E2ETests/TestResults/trazas-playwright/, que el paso «Publicar logs de CaeManager.Web si el E2E
/// falla» de ci.yml sube como artefacto (sube TestResults entero). Hasta entonces se escriben en un directorio
/// provisional que no se publica (ver <see cref="TramosDeTraza"/>): un tramo a medio escribir no llega al artefacto.
/// Se abre con <c>pwsh bin/Debug/net10.0/playwright.ps1 show-trace &lt;zip&gt;</c> o en trace.playwright.dev. Si no se
/// puede guardar, o el test falló antes de que empezara a grabar (en el inicio de sesión), el motivo queda en un
/// <c>…-sin-traza.txt</c> en ese directorio y en la salida de error. Un fallo de la traza (cualquier excepción salvo una
/// cancelación, que sí se propaga) nunca hace fallar el test ni tapa su excepción.
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
    private readonly TramosDeTraza _tramos;
    private bool _iniciada;
    private bool _grabando;
    private bool _superada;

    private TrazaAlFallar(IBrowserContext contexto, string nombre)
    {
        _contexto = contexto;
        _nombre = nombre;
        _tramos = new TramosDeTraza(nombre, Directorio(), DirectorioProvisional());
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
        // Deja de grabar aunque no se pueda guardar el tramo: lo que no puede pasar es que se guarde el acceso. Si el
        // corte falla, Playwright quizá siga grabando, pero lo que grabe solo se detiene sin ruta (DetenerAsync) y
        // TramosDeTraza ya ha borrado lo que el corte llegara a escribir; tampoco se vuelve a grabar en este contexto.
        traza._grabando = false;
        await traza._tramos.CortarAsync(
            ruta => contexto.Tracing.StopChunkAsync(new TracingStopChunkOptions { Path = ruta }),
            $"tramo{traza._tramos.Completos.Count + 1}");
    }

    /// <summary>Al acabar un inicio de sesión: la grabación empieza (o sigue, en un tramo nuevo) a partir de aquí.</summary>
    internal static async Task ReanudarTrasIniciarSesionAsync(IBrowserContext contexto)
    {
        if (!Armadas.TryGetValue(contexto, out var traza) || traza._grabando || traza._tramos.Averia is not null) return;
        try
        {
            if (traza._iniciada)
                await contexto.Tracing.StartChunkAsync(new TracingStartChunkOptions { Title = traza._nombre });
            else
                await contexto.Tracing.StartAsync(new TracingStartOptions { Title = traza._nombre, Screenshots = true, Snapshots = true });
            traza._iniciada = true;
            traza._grabando = true;
        }
        catch (Exception excepcion) when (excepcion is not OperationCanceledException)
        {
            traza._tramos.Averiar($"no se pudo empezar a grabar tras el inicio de sesión: {excepcion.GetType().Name}: {excepcion.Message}");
        }
    }

    /// <summary>Ruta en la que se publica la traza de un test fallido.</summary>
    public static string Directorio() => Path.Combine(DirectorioDelProyecto(), "TestResults", "trazas-playwright");

    /// <summary>Ruta en la que se escriben los tramos mientras el test no ha acabado; CI no la publica.</summary>
    public static string DirectorioProvisional() => Path.Combine(DirectorioDelProyecto(), "obj", "trazas-playwright-provisionales");

    private static string DirectorioDelProyecto()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir is null ? AppContext.BaseDirectory : Path.Combine(dir, "tests", "CaeManager.E2ETests");
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
            _tramos.Concluir(_superada, _iniciada
                ? "el test falló sin que quedara ningún tramo grabado"
                : "el test falló antes de acabar el inicio de sesión, y la traza no graba hasta entonces");
        }
    }

    private async Task DetenerAsync()
    {
        if (!_iniciada) return;
        if (!_superada && _grabando)
        {
            await _tramos.CortarAsync(ruta => _contexto.Tracing.StopAsync(new TracingStopOptions { Path = ruta }), "final");
            return;
        }

        try
        {
            // Sin ruta, Playwright descarta el tramo en curso (o no hay ninguno, si se cortó para otro inicio de sesión).
            await _contexto.Tracing.StopAsync(new TracingStopOptions { Path = null });
        }
        catch (Exception excepcion) when (excepcion is not OperationCanceledException)
        {
            // El contexto o el navegador ya se cerraron (p. ej. el test falló porque se cayó el navegador): tapar con esto
            // la excepción original del test sería peor que perder la traza. Si el test falló, Concluir lo anota.
            if (!_superada) _tramos.Averiar($"no se pudo detener la traza: {excepcion.GetType().Name}: {excepcion.Message}");
        }
    }
}
