using System.Text.RegularExpressions;

using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Ninguna plantilla de log de autenticación o de administración de usuarios
/// lleva el correo del usuario.
///
/// <para>
/// <b>Por qué importa.</b> Serilog persiste a fichero y a Seq: cada línea con
/// un correo replicaba un dato personal fuera de la base de datos, en
/// superficies con permisos y retención propios, ampliando el alcance RGPD de
/// logs, copias de seguridad e incidentes. Se sustituyó por el
/// <c>UsuarioId</c>, que es opaco, estable y ya existía — la correlación
/// forense no se pierde: seguir los intentos contra una misma cuenta sigue
/// siendo posible sin saber de quién es.
/// </para>
///
/// <para>
/// <b>Qué observa y qué no.</b> Observa el <i>texto de la plantilla</i>: un
/// marcador <c>{Email}</c>, <c>{Correo}</c> o <c>{Destinatario}</c> en una
/// llamada de logging. No observa que el argumento pasado no sea un correo
/// —<c>{UsuarioId}</c> alimentado con <c>usuario.Email</c> pasaría—, así que
/// no es una garantía sino un ratchet: impide la reincidencia por el camino
/// por el que ya ocurrió siete veces, que es el descuido de escribir el
/// marcador obvio.
/// </para>
///
/// <para>
/// El alcance son las carpetas de identidad y la de correo.
/// <c>GraphEmailService</c> estuvo fuera mientras registró la dirección del
/// destinatario al fallar un envío; el módulo de Comunicaciones lo cerró
/// (PR #370: los tres logs sin dirección, y los cuerpos de error de Graph
/// saneados con <c>CodigoErrorGraph</c>, que solo deja pasar el <c>code</c> y
/// nunca el <c>message</c>). Al entrar en <c>main</c>, aquella exclusión pasó
/// de ser una decisión a ser una puerta abierta, así que la carpeta entra aquí.
/// </para>
///
/// <para>
/// Los sembradores de demo siguen fuera, y esos sí por decisión: sus
/// direcciones son literales del repositorio, no datos de nadie.
/// </para>
/// </summary>
public class SinCorreosEnLogsDeIdentidadTests
{
    private static readonly string[] CarpetasVigiladas =
    [
        "src/CaeManager.Web/Components/Account",
        "src/CaeManager.Web/Features/Usuarios",
        "src/CaeManager.Web/Features/GestionRoles",

        // Entra al cerrarse el hallazgo del módulo de Comunicaciones (PR #370).
        // Mientras GraphEmailService registró la dirección del destinatario,
        // vigilarla habría dado rojo por un uso que su dueño todavía no había
        // decidido; ahora que está corregido, dejarla fuera solo dejaría la
        // puerta abierta a que vuelva.
        "src/CaeManager.Infrastructure/Email",
    ];

    private static readonly Regex PlantillaConCorreo = new(
        @"Log(Information|Warning|Error|Critical|Debug|Trace)\s*\(.*\{(Email|Correo|Destinatario)\}",
        RegexOptions.Compiled);

    [Fact]
    public void Ningun_log_de_identidad_lleva_el_correo_del_usuario()
    {
        var infractores = ArchivosVigilados()
            .SelectMany(archivo => File.ReadLines(archivo.Ruta)
                .Select((linea, indice) => (linea, numero: indice + 1))
                .Where(l => PlantillaConCorreo.IsMatch(l.linea))
                .Select(l => $"{archivo.Relativa}:{l.numero}"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        string.Join(Environment.NewLine, infractores).Should().BeEmpty(
            "un correo en una plantilla de log sale de la base de datos hacia el fichero de Serilog y hacia Seq, " +
            "con permisos y retención distintos; usa el UsuarioId, que identifica igual de bien y no es un dato " +
            "personal. Si de verdad hace falta la dirección para diagnosticar, no basta con renombrar el " +
            "marcador: justifícalo en este mismo test");
    }

    /// <summary>
    /// Guarda del propio ratchet: si el patrón dejara de encontrar el caso que
    /// vino a impedir, estaría vigilando algo que ya no sabe reconocer y daría
    /// verde por no mirar. Se comprueba contra una línea sintética, no contra
    /// el árbol —donde por definición ya no queda ninguna—, que es la única
    /// forma de distinguir "no hay infractores" de "no sé verlos".
    /// </summary>
    [Fact]
    public void El_patron_reconoce_el_caso_que_vino_a_impedir()
    {
        PlantillaConCorreo.IsMatch(
            @"logger.LogWarning(""Login fallido (credenciales inválidas): {Email}"", Entrada.Email);")
            .Should().BeTrue();

        PlantillaConCorreo.IsMatch(
            @"logger.LogInformation(""Login correcto: {UsuarioId}"", idCuenta);")
            .Should().BeFalse("el reemplazo correcto no puede dar positivo, o el ratchet sería inservible");
    }

    /// <summary>
    /// <b>Cada</b> carpeta vigilada existe y aporta ficheros, no solo el
    /// conjunto en total.
    ///
    /// <para>
    /// La versión anterior solo exigía que el resultado global no fuera vacío,
    /// y eso no basta: con cuatro carpetas en la lista, una errata en una sola
    /// —o un renombrado— la deja aportando cero ficheros mientras las otras
    /// tres sostienen el verde. El escaneo diría "sin infracciones" sobre una
    /// carpeta que no está mirando, que es exactamente la clase de resultado
    /// vacío que no es una ausencia. Se comprueba una a una para que la que
    /// falle se nombre.
    /// </para>
    /// </summary>
    [Fact]
    public void Cada_carpeta_vigilada_existe_y_aporta_ficheros()
    {
        var vacias = CarpetasVigiladas
            .Where(c => ArchivosVigilados().All(a => !a.Relativa.StartsWith(c, StringComparison.Ordinal)))
            .ToList();

        string.Join(Environment.NewLine, vacias).Should().BeEmpty(
            "una carpeta que no aporta ni un fichero no se está escaneando —ruta mal escrita, movida o " +
            "renombrada— y su verde no significa que no haya correos en sus logs, sino que nadie ha mirado");
    }

    private static List<(string Ruta, string Relativa)> ArchivosVigilados()
    {
        var raiz = RaizDelRepositorio();

        return CarpetasVigiladas
            .Select(c => Path.Combine(raiz, c.Replace('/', Path.DirectorySeparatorChar)))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            .Where(a => a.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || a.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(a => !a.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !a.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(a => (a, Path.GetRelativePath(raiz, a).Replace(Path.DirectorySeparatorChar, '/')))
            .ToList();
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
