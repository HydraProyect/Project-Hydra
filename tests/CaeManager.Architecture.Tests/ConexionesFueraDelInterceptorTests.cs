using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// El enforcement de solo lectura del plano 3 se aplica en
/// <c>TenantRlsConnectionInterceptor</c>, al abrir la conexión. Toda su
/// garantía descansa en una premisa: que <b>no exista otra forma</b> de llegar a
/// la base de datos. Una conexión abierta al margen de EF no pasa por ningún
/// interceptor, así que no lleva ni el <c>SET ROLE</c> del rol de soporte ni el
/// <c>app.tenant_id</c> del que dependen las políticas de RLS — una sesión de
/// soporte que llegara por ahí escribiría, y además vería todos los tenants.
///
/// La premisa se comprobó a mano al construir F2b-3 y era cierta. Este test la
/// mantiene cierta: una conexión nueva abierta por fuera hace fallar el build en
/// vez de desarmar el control en silencio.
///
/// Mismo mecanismo de ratchet por texto que
/// <see cref="AccesoRestringidoACatalogosDeAsignacionTests"/>: son llamadas, no
/// dependencias de tipo, así que la reflexión sobre el ensamblado no las ve.
/// </summary>
public class ConexionesFueraDelInterceptorTests
{
    /// <summary>
    /// Las formas de conseguir una conexión sin pasar por EF.
    ///
    /// <para>
    /// La primera versión de este patrón vigilaba un constructor concreto y dos
    /// nombres de tipo. Dos formas triviales la esquivaban compilando,
    /// demostradas por mutación: el constructor <b>cualificado</b> (que no casa
    /// con un patrón escrito sin el prefijo del espacio de nombres) y la
    /// <b>fábrica estática</b> de la fuente de datos (que no es el Builder).
    /// Las dos abren una conexión real, sin <c>app.tenant_id</c> y sin el
    /// <c>SET ROLE</c> de solo lectura.
    /// </para>
    ///
    /// <para>
    /// Vigilar un constructor medía una propiedad más estrecha que la que este
    /// ratchet promete, que es <b>que no haya otra forma de llegar a la base de
    /// datos</b>. Ahora se vigila el tipo entero de la fuente de datos —cubre el
    /// Builder, la fábrica estática y una fuente inyectada—, el constructor con
    /// o sin cualificar, y los dos verbos que devuelven una conexión viva, que
    /// es por donde se escaparía una fuente que llegara por inyección sin que
    /// su tipo se nombre nunca en el fichero.
    /// </para>
    /// </summary>
    private static readonly Regex PatronConexionCruda = new(
        @"new\s+(?:Npgsql\.)?NpgsqlConnection\b|\bNpgsqlDataSource\b|\bGetDbConnection\s*\("
        + @"|\bOpenConnectionAsync\s*\(|\bCreateConnection\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Los únicos sitios que abren una conexión por su cuenta, con lo que hace
    /// cada uno y por qué no rompe la premisa.
    /// </summary>
    private static readonly HashSet<string> ArchivosAutorizados =
    [
        // Elección de líder para trabajos de fondo: pide un advisory lock
        // (pg_try_advisory_lock) y nada más. No lee ni escribe ninguna tabla,
        // así que no hay filas que aislar ni escritura que impedir. Corre en un
        // servicio de fondo, sin petición ni usuario en juego.
        "src/CaeManager.Infrastructure/Coordinacion/EleccionLiderPostgresService.cs",

        // Comprobación de arranque de que la identidad de conexión del tráfico
        // está sometida a RLS. No lee ninguna tabla de negocio: solo
        // current_user, sus atributos en pg_roles y si es propietaria de alguna
        // tabla con RLS — metadatos de la sesión, no filas que aislar, así que
        // no hay nada que app.tenant_id pudiera filtrar.
        //
        // Y el SET ROLE de soporte sería justamente lo que la invalidaría:
        // adoptar otro rol cambia current_user, de modo que la comprobación
        // mediría la identidad adoptada en vez de aquella con la que la
        // aplicación autentica, que es la única que decide si PostgreSQL aplica
        // las políticas. Pasar por el interceptor no la haría más segura: la
        // dejaría ciega a lo que existe para observar.
        "src/CaeManager.Infrastructure/Persistence/VerificacionIdentidadDeRuntime.cs",

        // Verificación de arranque (auditoría Módulo 8): lee pg_roles para
        // confirmar si el rol de CaeManagerDbRuntime está restringido de
        // verdad (rolsuper/rolbypassrls). Es un catálogo de sistema de
        // PostgreSQL, no una tabla de dominio con TenantId — no hay ninguna
        // fila de tenant que aislar ni escritura de aplicación que impedir.
        // Corre una sola vez al arrancar, sin petición ni usuario en juego,
        // igual que EleccionLiderPostgresService.
        //
        // DUPLICIDAD CONOCIDA, pendiente de resolver con el Módulo 8: este
        // servicio y VerificacionIdentidadDeRuntime (arriba) comprueban lo
        // mismo y toman decisiones OPUESTAS — aquel aborta el arranque, este
        // avisa y sigue a propósito. Ambos entran en la lista para que el
        // ratchet no dé un falso positivo mientras se decide cuál sobrevive;
        // dos entradas aquí no son una bendición al diseño, solo constancia de
        // que las dos abren conexión.
        "src/CaeManager.Infrastructure/Persistence/VerificacionRolRuntimeHostedService.cs",
    ];

    [Fact]
    public void Solo_los_puntos_autorizados_abren_conexiones_al_margen_de_EF()
    {
        var raiz = RaizDelRepositorio();
        var carpetas = new[] { "src/CaeManager.Application", "src/CaeManager.Infrastructure", "src/CaeManager.Web" };

        var infractores = new List<string>();

        foreach (var carpeta in carpetas)
        {
            var directorio = Path.Combine(raiz, carpeta.Replace('/', Path.DirectorySeparatorChar));

            // Los .razor entran igual que los code-behind: un bloque @code es C#.
            // obj/ y bin/ quedan fuera para no contar el C# que el compilador genera
            // a partir de cada .razor como si fuera un acceso más.
            var archivos = Directory
                .EnumerateFiles(directorio, "*", SearchOption.AllDirectories)
                .Where(a => a.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                            || a.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                .Where(a => !a.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                            && !a.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

            foreach (var archivo in archivos)
            {
                var rutaRelativa = Path.GetRelativePath(raiz, archivo).Replace(Path.DirectorySeparatorChar, '/');
                if (ArchivosAutorizados.Contains(rutaRelativa)) continue;

                if (File.ReadLines(archivo).Any(linea => PatronConexionCruda.IsMatch(linea)))
                    infractores.Add(rutaRelativa);
            }
        }

        string.Join(Environment.NewLine, infractores.OrderBy(x => x)).Should().BeEmpty(
            "una conexión abierta fuera de EF no pasa por TenantRlsConnectionInterceptor, así que no lleva " +
            "app.tenant_id (y RLS no la filtra) ni el SET ROLE de solo lectura (y una sesión de soporte podría " +
            "escribir por ahí); si el acceso está justificado, añádelo a ArchivosAutorizados en este mismo commit " +
            "explicando qué toca y por qué no necesita ninguna de las dos cosas");
    }

    /// <summary>
    /// Guarda del propio test: si el patrón dejara de encontrar los accesos ya
    /// conocidos, estaría vigilando algo que ya no existe.
    /// </summary>
    [Fact]
    public void Hay_accesos_autorizados_que_inspeccionar()
    {
        var raiz = RaizDelRepositorio();

        var encontrados = ArchivosAutorizados.Count(ruta =>
        {
            var archivo = Path.Combine(raiz, ruta.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(archivo) && File.ReadLines(archivo).Any(l => PatronConexionCruda.IsMatch(l));
        });

        encontrados.Should().Be(ArchivosAutorizados.Count,
            "cada archivo de la lista tiene que seguir abriendo una conexión cruda; si ya no lo hace, sobra de la " +
            "lista y dejarlo ahí solo relaja el ratchet");
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
                " — este test necesita el árbol fuente del repositorio, no solo los ensamblados compilados.");

        return actual.FullName;
    }
}
