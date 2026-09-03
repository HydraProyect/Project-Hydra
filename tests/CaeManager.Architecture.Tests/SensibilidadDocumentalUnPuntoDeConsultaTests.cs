using System.Text.RegularExpressions;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// DEC-34/36 (REC-132): «no introduzcáis reglas jurídicas dispersas mediante
/// <c>if TipoDocumento == ...</c>». Este ratchet vigila esa prohibición
/// literal para el eje de sensibilidad documental: el <b>único</b> sitio del
/// código permitido para decidir si un tipo revela salud comparando su
/// <c>Nombre</c> es la clasificación propuesta del catálogo semilla
/// (<c>TipoDocumentoSeedData.SensibilidadPorNombre</c>) — todo lo demás debe
/// consultar <c>TipoDocumento.Sensibilidad</c> / <c>TipoDocumento.RevelaSalud</c>.
///
/// <para>
/// Sin este ratchet, REC-036 (purga de derivados de IA) o REC-099 (auditoría
/// de acceso) podrían nacer comparando el nombre del documento en vez de
/// consultar el eje — exactamente el patrón que la clasificación canónica
/// existe para sustituir, y que además se rompería en silencio en cuanto
/// alguien renombrase un tipo (ver <c>NaturalezaDelCatalogoSemillaTests</c> y
/// la memoria de T1 sobre ese mismo riesgo).
/// </para>
///
/// <para>
/// ⚠️ <b>Cobertura declarada, no exhaustiva</b>: es un ratchet de texto por
/// línea, como el resto de esta clase de tests en el repositorio (ver
/// <c>ConsultasDeSecretosMarcadasTests</c>) — no un análisis de árbol
/// sintáctico. Detecta comparaciones directas (<c>==</c>, <c>!=</c>,
/// <c>Contains</c>, <c>StartsWith</c>, <c>EndsWith</c>, <c>Equals</c>) contra
/// los nombres <b>exactos</b> ya clasificados como categoría especial de
/// salud — no vocabulario adivinado, para no fallar en silencio ante un tipo
/// como "Informe de investigación de accidente o incidente" que no contiene
/// ninguna palabra de salud en su nombre. Una comparación multi-línea, un
/// `switch` sobre el nombre, o una constante indirecta se le escaparían: si
/// se descubre un caso así, se refuerza el patrón, no se ignora el hueco.
/// </para>
/// </summary>
public class SensibilidadDocumentalUnPuntoDeConsultaTests
{
    /// <summary>
    /// Único archivo exento, no todo el directorio de seeds:
    /// <c>DatosPruebaSeeder</c> localiza "Certificado de aptitud médica" por
    /// nombre para colgarle un <c>Documento</c> de prueba — una búsqueda
    /// legítima de datos de ensayo, no una decisión de sensibilidad.
    /// <c>TipoDocumentoSeedData.cs</c> (donde vive la clasificación
    /// autorizada) sigue vigilado por este mismo test: su propia
    /// clasificación es una asignación a diccionario
    /// (<c>["nombre"] = (valor, motivo)</c>), no una comparación, así que no
    /// dispara el patrón — y cualquier comparación que se añadiera allí FUERA
    /// del diccionario sí debe seguir cayendo bajo este ratchet.
    /// </summary>
    private const string ArchivoExento = "src/CaeManager.Infrastructure/Persistence/Seed/DatosPruebaSeeder.cs";

    private static Regex ConstruirPatron(IReadOnlyCollection<string> nombresDeSalud)
    {
        var alternativas = string.Join("|", nombresDeSalud.Select(Regex.Escape));

        return new Regex(
            $"""(\.Nombre\s*(==|!=)\s*"(?:{alternativas})")""" +
            $"""|("(?:{alternativas})"\s*(==|!=)\s*\S*\.Nombre\b)""" +
            $"""|(\.Nombre\.(Contains|StartsWith|EndsWith|Equals)\("(?:{alternativas})"\))""",
            RegexOptions.Compiled);
    }

    [Fact]
    public void Ningun_archivo_fuera_del_catalogo_semilla_decide_salud_comparando_el_nombre()
    {
        // Los nombres que HOY revelan salud, leídos del propio catálogo — no
        // una lista mantenida a mano que pueda desincronizarse: si mañana se
        // añade un tercer tipo a CategoriaEspecialSalud, este test empieza a
        // vigilar su nombre sin tocar el test.
        var nombresDeSalud = TipoDocumentoSeedData.CrearCopiasParaTenant()
            .Where(t => t.RevelaSalud)
            .Select(t => t.Nombre)
            .ToList();

        nombresDeSalud.Should().NotBeEmpty("sin nombres que vigilar, este test pasaría en vacío");

        var patron = ConstruirPatron(nombresDeSalud);
        var raiz = RaizDelRepositorio();
        var directorio = Path.Combine(raiz, "src");

        var infractores = Directory
            .EnumerateFiles(directorio, "*.cs", SearchOption.AllDirectories)
            .Select(archivo => (Ruta: Path.GetRelativePath(raiz, archivo).Replace(Path.DirectorySeparatorChar, '/'), archivo))
            .Where(x => !x.Ruta.Equals(ArchivoExento, StringComparison.OrdinalIgnoreCase))
            .Where(x => File.ReadLines(x.archivo).Any(linea => patron.IsMatch(linea)))
            .Select(x => x.Ruta)
            .OrderBy(x => x)
            .ToList();

        string.Join(Environment.NewLine, infractores).Should().BeEmpty(
            "decidir si un documento revela salud comparando TipoDocumento.Nombre es exactamente la regla jurídica " +
            "dispersa que DEC-34/36 prohíbe; consultar TipoDocumento.Sensibilidad / TipoDocumento.RevelaSalud en su lugar, " +
            "o añadir el tipo (con motivo) a TipoDocumentoSeedData.SensibilidadPorNombre si es una propuesta de catálogo");

        // Guarda del propio test: si el archivo exento dejara de existir, la
        // ruta estaría vigilando algo que ya no hay.
        File.Exists(Path.Combine(raiz, ArchivoExento.Replace('/', Path.DirectorySeparatorChar))).Should().BeTrue();
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
