using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Las peticiones que salen de <c>src/CaeManager.Infrastructure/AsistenteIa</c>
/// llevan el contenido de documentos de clientes: el texto de un apto médico,
/// un listado de plantilla con nombres y DNI, el cuerpo de un correo. Cuando el
/// proveedor responde con un error, su cuerpo puede incluir fragmentos de la
/// solicitud que lo provocó — es decir, de esos mismos datos.
///
/// Hasta este ratchet, los nueve puntos de error del directorio hacían
/// <c>ReadAsStringAsync()</c> y volcaban ese cuerpo entero al log. Cada fallo
/// del proveedor era así una copia de datos personales replicada a los ficheros
/// de log, a Sentry y a los backups de ambos, cada destino con su retención y
/// su lista de personas con acceso, y ninguna de las dos cosas decidida por el
/// tenant dueño de los datos. De regalo, log forging: el cuerpo lo escribe un
/// tercero.
///
/// Lo que se registra ahora es código de estado e identificador de correlación
/// (ver <c>CorrelacionRespuestaIa</c>), que basta para distinguir credencial de
/// cuota de caída y para que el proveedor busque la petición en su lado, donde
/// el cuerpo puede consultarse sin copiarlo a ninguna parte.
///
/// Mismo mecanismo de ratchet por texto que
/// <see cref="ConexionesFueraDelInterceptorTests"/>: son llamadas, no
/// dependencias de tipo, así que la reflexión sobre el ensamblado no las ve.
/// </summary>
public class CuerposDeProveedorIaFueraDeLosLogsTests
{
    private const string CarpetaVigilada = "src/CaeManager.Infrastructure/AsistenteIa";

    /// <summary>
    /// Las dos mitades del defecto, vigiladas por separado a propósito.
    ///
    /// <para>
    /// <c>ReadAsStringAsync</c> es la lectura del cuerpo como texto crudo. Es
    /// el paso que hay que no dar: mientras el cuerpo no se materialice en una
    /// variable, no hay nada que pueda acabar en un log por descuido. Las
    /// respuestas correctas se leen con <c>ReadFromJsonAsync</c>, que no
    /// necesita este paso.
    /// </para>
    ///
    /// <para>
    /// El marcador <c>{Cuerpo}</c> cubre la otra dirección: alguien que
    /// obtenga el texto por otra vía (un <c>ReadAsStream</c>, una propiedad de
    /// una excepción del SDK) y lo interpole igualmente en la plantilla del
    /// log. Vigilar solo la lectura mediría una propiedad más estrecha que la
    /// que este ratchet promete, que es <b>que el cuerpo remoto no llegue a los
    /// registros</b>.
    /// </para>
    /// </summary>
    private static readonly Regex PatronCuerpoRemoto = new(
        @"\bReadAsStringAsync\s*\(|\{Cuerpo\}",
        RegexOptions.Compiled);

    /// <summary>
    /// Los comentarios quedan fuera: <c>CorrelacionRespuestaIa</c> explica en
    /// su documentación cuál era el defecto, y nombrarlo para explicarlo no es
    /// cometerlo. Sin esta exclusión, el ratchet obligaría a documentar el
    /// problema en circunloquios o a no documentarlo — que es justo cómo se
    /// pierde el porqué de una corrección.
    /// </summary>
    private static bool EsCodigoQueVuelcaElCuerpo(string linea)
    {
        var contenido = linea.TrimStart();

        if (contenido.StartsWith("//", StringComparison.Ordinal)
            || contenido.StartsWith("*", StringComparison.Ordinal)
            || contenido.StartsWith("/*", StringComparison.Ordinal))
        {
            return false;
        }

        return PatronCuerpoRemoto.IsMatch(linea);
    }

    [Fact]
    public void Ningun_proveedor_de_ia_vuelca_el_cuerpo_de_la_respuesta_a_los_logs()
    {
        var raiz = RaizDelRepositorio();
        var directorio = Path.Combine(raiz, CarpetaVigilada.Replace('/', Path.DirectorySeparatorChar));

        Directory.Exists(directorio).Should().BeTrue(
            "si el directorio cambia de sitio, este ratchet deja de vigilar nada y hay que reapuntarlo");

        var infractores = Directory
            .EnumerateFiles(directorio, "*.cs", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !a.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(a => File.ReadLines(a).Any(EsCodigoQueVuelcaElCuerpo))
            .Select(a => Path.GetRelativePath(raiz, a).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(x => x)
            .ToList();

        string.Join(Environment.NewLine, infractores).Should().BeEmpty(
            "el cuerpo de error de un proveedor de IA puede contener fragmentos de la solicitud, y esa solicitud " +
            "lleva el texto del documento del cliente: nombres, DNI, datos de salud. Registra estado y " +
            "CorrelacionRespuestaIa.Describir(respuesta) en su lugar; si de verdad hace falta el cuerpo para " +
            "diagnosticar, pídeselo al proveedor con ese identificador");
    }

    /// <summary>
    /// Guarda del propio instrumento. Un ratchet por texto cuyo patrón no case
    /// con nada da verde para siempre y no vigila nada — y como aquí la lista
    /// de infractores esperada es vacía, el test principal no puede distinguir
    /// "no hay infractores" de "el regex está roto". Esta comprobación separa
    /// las dos cosas: el patrón tiene que seguir reconociendo las dos formas
    /// exactas del defecto que se corrigió.
    /// </summary>
    [Fact]
    public void El_patron_reconoce_las_dos_formas_del_defecto_que_vigila()
    {
        const string lecturaDelCuerpo =
            "                var cuerpoError = await respuesta.Content.ReadAsStringAsync(cancellationToken);";
        const string volcadoEnLog =
            "                    \"La API de Anthropic devolvió {StatusCode}: {Cuerpo}\", (int)respuesta.StatusCode, cuerpoError);";
        const string formaCorrecta =
            "                    \"La API de Anthropic devolvió {StatusCode} ({Correlacion}).\", (int)respuesta.StatusCode, CorrelacionRespuestaIa.Describir(respuesta));";

        const string mencionEnComentario =
            "    /// hacía <c>ReadAsStringAsync()</c> y volcaba el cuerpo entero al log.";

        EsCodigoQueVuelcaElCuerpo(lecturaDelCuerpo).Should().BeTrue("es la lectura del cuerpo que se retiró");
        EsCodigoQueVuelcaElCuerpo(volcadoEnLog).Should().BeTrue("es el volcado en la plantilla del log que se retiró");
        EsCodigoQueVuelcaElCuerpo(formaCorrecta).Should().BeFalse("la forma correcta no puede disparar el ratchet");
        EsCodigoQueVuelcaElCuerpo(mencionEnComentario).Should().BeFalse("documentar el defecto no es cometerlo");
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
