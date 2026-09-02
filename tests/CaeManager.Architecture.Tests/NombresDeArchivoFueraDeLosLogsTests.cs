using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// El nombre de archivo que sube un tenant puede llevar dato de categoría
/// especial (art. 9 RGPD): nombre de trabajador, DNI, tipo de aptitud
/// médica — es habitual que el propio fichero se llame así. Dos sitios lo
/// interpolaban crudo en la plantilla del log: <c>IngestaWebhookService</c>
/// al descargar un adjunto de correo y <c>PdfSharpClasificadorDocumentoService</c>
/// al fallar la clasificación de un PDF. Mismo defecto que
/// <see cref="CuerposDeProveedorIaFueraDeLosLogsTests"/> cerró para el
/// cuerpo de respuesta de un proveedor de IA (PR #383): un dato de cliente
/// terminaba replicado en logs, Sentry y sus backups sin que el tenant dueño
/// del dato decidiera nada de eso.
///
/// Lo que queda en su lugar es un identificador de correlación sin PHI: el
/// id externo del adjunto de Graph, o el mismo hash SHA-256 del contenido
/// que <c>DocumentAIRouterService</c> ya guarda en <c>AuditoriaExtraccionIa</c>
/// para ese mismo fallo — basta para localizar el registro sin copiar el
/// nombre.
///
/// Mismo mecanismo de ratchet por texto que
/// <see cref="CuerposDeProveedorIaFueraDeLosLogsTests"/>: es una plantilla de
/// interpolación de string, no una dependencia de tipo, así que la reflexión
/// sobre el ensamblado no la ve.
/// </summary>
public class NombresDeArchivoFueraDeLosLogsTests
{
    private static readonly string[] FicherosVigilados =
    [
        "src/CaeManager.Application/Integraciones/IngestaWebhookService.cs",
        "src/CaeManager.Infrastructure/DocumentosIa/PdfSharpClasificadorDocumentoService.cs",
    ];

    /// <summary>
    /// El marcador de plantilla, no la variable en sí: <c>nombreArchivo</c>
    /// sigue siendo un parámetro legítimo (decide si es imagen, se pasa al
    /// clasificador...). Lo prohibido es que aparezca como argumento
    /// interpolado de un mensaje de log, y eso se ve en el propio marcador de
    /// la plantilla — no hace falta parsear qué argumento posicional le
    /// corresponde.
    /// </summary>
    private static readonly Regex PatronNombreArchivoEnLog = new(
        @"\{NombreArchivo\}",
        RegexOptions.Compiled);

    private static bool EsCodigoQueVuelcaElNombre(string linea)
    {
        var contenido = linea.TrimStart();

        if (contenido.StartsWith("//", StringComparison.Ordinal)
            || contenido.StartsWith("*", StringComparison.Ordinal)
            || contenido.StartsWith("/*", StringComparison.Ordinal))
        {
            return false;
        }

        return PatronNombreArchivoEnLog.IsMatch(linea);
    }

    [Fact]
    public void Ningun_log_de_ingesta_o_clasificacion_interpola_el_nombre_de_archivo()
    {
        var raiz = RaizDelRepositorio();
        var rutas = FicherosVigilados
            .Select(relativo => (Relativo: relativo, Absoluto: Path.Combine(raiz, relativo.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();

        foreach (var (relativo, absoluto) in rutas)
        {
            File.Exists(absoluto).Should().BeTrue(
                $"si {relativo} cambia de sitio, este ratchet deja de vigilar nada y hay que reapuntarlo");
        }

        var infractores = rutas
            .Where(par => File.ReadLines(par.Absoluto).Any(EsCodigoQueVuelcaElNombre))
            .Select(par => par.Relativo)
            .OrderBy(x => x)
            .ToList();

        string.Join(Environment.NewLine, infractores).Should().BeEmpty(
            "el nombre de archivo que sube un tenant puede llevar dato de categoría especial (nombre de " +
            "trabajador, DNI, tipo de aptitud médica); registra un identificador sin PHI en su lugar (id externo " +
            "del adjunto, hash SHA-256 del contenido)");
    }

    /// <summary>
    /// Guarda del propio instrumento: si el patrón no reconociera la forma
    /// exacta del defecto que se corrigió, el test principal daría verde para
    /// siempre sin vigilar nada.
    /// </summary>
    [Fact]
    public void El_patron_reconoce_la_forma_del_defecto_que_vigila()
    {
        const string volcadoEnLog =
            "                \"Adjunto {NombreArchivo} del mensaje {MensajeId} omitido: {TamanoBytes} bytes supera el máximo de {Maximo}.\",";
        const string formaCorrecta =
            "                \"Adjunto {AdjuntoId} del mensaje {MensajeId} omitido: {TamanoBytes} bytes supera el máximo de {Maximo}.\",";
        const string mencionEnComentario =
            "        // No se interpola nombreArchivo: el nombre de archivo que sube un tenant puede llevar {NombreArchivo}.";

        EsCodigoQueVuelcaElNombre(volcadoEnLog).Should().BeTrue("es el volcado en la plantilla del log que se retiró");
        EsCodigoQueVuelcaElNombre(formaCorrecta).Should().BeFalse("la forma correcta no puede disparar el ratchet");
        EsCodigoQueVuelcaElNombre(mencionEnComentario).Should().BeFalse("documentar el defecto no es cometerlo");
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        actual.Should().NotBeNull("los tests tienen que correr dentro del repositorio");
        return actual!.FullName;
    }
}
