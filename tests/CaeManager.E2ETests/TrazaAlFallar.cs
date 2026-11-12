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
    private readonly GrabacionDeTraza _grabacion;

    private TrazaAlFallar(IBrowserContext contexto, string nombre)
    {
        _contexto = contexto;
        // Las decisiones (cuándo grabar, qué guardar, qué publicar) viven en GrabacionDeTraza y TramosDeTraza, sin
        // Playwright, para probarlas sin navegador en CaeManager.Web.Tests; aquí solo se conectan con contexto.Tracing.
        _grabacion = new GrabacionDeTraza(
            new TramosDeTraza(nombre, Directorio(), DirectorioProvisional()),
            new OperacionesDeTraza(
                Empezar: () => contexto.Tracing.StartAsync(new TracingStartOptions { Title = nombre, Screenshots = true, Snapshots = true }),
                EmpezarTramo: () => contexto.Tracing.StartChunkAsync(new TracingStartChunkOptions { Title = nombre }),
                CortarTramo: ruta => contexto.Tracing.StopChunkAsync(new TracingStopChunkOptions { Path = ruta }),
                DetenerGuardando: ruta => contexto.Tracing.StopAsync(new TracingStopOptions { Path = ruta }),
                DetenerDescartando: () => contexto.Tracing.StopAsync(new TracingStopOptions { Path = null })));
    }

    public static TrazaAlFallar Armar(
        IBrowserContext contexto, [CallerMemberName] string prueba = "", [CallerFilePath] string archivo = "")
    {
        var traza = new TrazaAlFallar(contexto, $"{Path.GetFileNameWithoutExtension(archivo)}.{prueba}");
        Armadas.AddOrUpdate(contexto, traza);
        return traza;
    }

    /// <summary>El test llegó al final: la traza se descarta.</summary>
    public void Superada() => _grabacion.Superada();

    /// <summary>
    /// Al empezar un inicio de sesión: si el contexto ya estaba grabando, el tramo grabado hasta aquí se guarda aparte
    /// (se borra si el test acaba superado) y se deja de grabar hasta <see cref="ReanudarTrasIniciarSesionAsync"/>.
    /// </summary>
    internal static Task PausarParaIniciarSesionAsync(IBrowserContext contexto) =>
        Armadas.TryGetValue(contexto, out var traza) ? traza._grabacion.PausarAsync() : Task.CompletedTask;

    /// <summary>Al acabar un inicio de sesión: la grabación empieza (o sigue, en un tramo nuevo) a partir de aquí.</summary>
    internal static Task ReanudarTrasIniciarSesionAsync(IBrowserContext contexto) =>
        Armadas.TryGetValue(contexto, out var traza) ? traza._grabacion.ReanudarAsync() : Task.CompletedTask;

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
        await _grabacion.TerminarAsync();
    }
}
