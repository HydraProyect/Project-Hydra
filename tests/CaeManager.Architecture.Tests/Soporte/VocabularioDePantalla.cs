using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CaeManager.Architecture.Tests;

/// <summary>Un término canónico de pantalla: la forma con su apellido semántico y las variantes que el contrato permite.</summary>
internal sealed record TerminoCanonico(
    string Id,
    string Forma,
    string? Plural,
    IReadOnlyList<string>? FormasCortasPermitidas,
    string Significado,
    string Contrato,
    string? NoConfundirCon);

/// <summary>
/// Un término que la pantalla no puede decir a secas: <see cref="Patron"/> es el patrón (regex .NET y
/// Python a la vez: solo <c>\b</c>, grupos y anticipaciones) que casa con la forma NO permitida, de modo
/// que las completaciones legítimas («Cliente empresarial») ya quedan fuera del patrón. <see cref="Casa"/> y
/// <see cref="NoCasa"/> son los ejemplos que el propio fichero lleva como control positivo y negativo de su
/// patrón: una regla sin ejemplos que deba cazar no se puede distinguir de una regla ciega.
/// </summary>
internal sealed record TerminoProhibido(
    string Id,
    string Termino,
    string Patron,
    bool IgnorarMayusculas,
    string Sustitucion,
    string Motivo,
    string Contrato,
    string? Sentido,
    IReadOnlyList<string>? Casa,
    IReadOnlyList<string>? NoCasa);

/// <summary>
/// Una excepción por contexto: dónde un término prohibido SÍ puede aparecer, y por qué. Sin motivo no hay
/// excepción. <see cref="Ficheros"/> son rutas desde la raíz del repositorio con comodines (<c>*</c> no
/// cruza <c>/</c>, <c>**</c> sí); <see cref="Claves"/> restringe un <c>.resx</c> a esas claves.
/// </summary>
internal sealed record ExcepcionDeVocabulario(
    string Id,
    IReadOnlyList<string> Prohibidos,
    IReadOnlyList<string> Ficheros,
    IReadOnlyList<string>? Claves,
    string Contexto,
    string Motivo);

internal sealed record VocabularioJson(
    int Version,
    string Fuente,
    IReadOnlyList<TerminoCanonico> Canonicos,
    IReadOnlyList<TerminoProhibido> Prohibidos,
    IReadOnlyList<ExcepcionDeVocabulario> Excepciones);

/// <summary>Una aparición de un término prohibido en el texto visible de un fichero.</summary>
internal sealed record HallazgoDeVocabulario(
    string Fichero,
    string? Clave,
    string IdProhibido,
    string Coincidencia,
    string? IdExcepcion)
{
    /// <summary>
    /// La pareja que congela <see cref="ListaCongelada"/>: el fichero y, en un <c>.resx</c>, la clave y el
    /// término; en un <c>.razor</c> no hay clave, solo el término.
    /// </summary>
    public Ubicacion Ubicacion => new(Fichero, Clave is null ? $"[{IdProhibido}]" : $"{Clave} [{IdProhibido}]");
}

/// <summary>
/// Vocabulario de pantalla ejecutable (S1 del análisis de causas raíz de 2026-10-02). La <b>fuente
/// única</b> es <c>Vocabulario/Vocabulario.json</c>; este tipo la carga y aplica a los valores de los
/// <c>.resx</c> (neutral y satélites es/ca) y al texto visible de los <c>.razor</c>. La tabla del contrato
/// de terminología no se escribe: la genera <c>scripts/vocabulario-tabla.py</c> desde ese mismo fichero.
///
/// <para>
/// <b>Qué ve.</b> El valor de cada <c>&lt;data&gt;&lt;value&gt;</c> de todo <c>.resx</c> de <c>src</c>, y,
/// de cada <c>.razor</c>, el texto de interfaz que está escrito a mano en el marcado (el mismo detector
/// que <c>TextosSinLocalizarCongeladosTests</c>: texto entre etiquetas, atributos de texto conocidos y
/// literales C# que parecen lenguaje natural).
/// </para>
///
/// <para>
/// <b>Qué no ve, declarado.</b> Texto que llega de datos (catálogos sembrados, mensajes de
/// <c>Result</c> de Application, excepciones), literales de <c>.cs</c> (incluido Application), texto
/// montado en JavaScript o por concatenación, y una palabra suelta en minúscula dentro de un literal C#
/// del <c>.razor</c> que el detector de lenguaje natural no reconoce como tal.
/// </para>
/// </summary>
internal static class VocabularioDePantalla
{
    public const string RutaRelativa = "tests/CaeManager.Architecture.Tests/Vocabulario/Vocabulario.json";

    private static readonly JsonSerializerOptions Opciones = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static string RutaDelJson() => Path.Combine(FuentesDeSrc.RaizDelRepositorio(), RutaRelativa.Replace('/', Path.DirectorySeparatorChar));

    public static VocabularioJson Cargar() => Leer(File.ReadAllText(RutaDelJson()));

    public static VocabularioJson Leer(string json) =>
        JsonSerializer.Deserialize<VocabularioJson>(json, Opciones)
        ?? throw new InvalidOperationException("Vocabulario.json vacío.");

    public static Regex Compilar(TerminoProhibido t) =>
        new(t.Patron, RegexOptions.CultureInvariant | (t.IgnorarMayusculas ? RegexOptions.IgnoreCase : RegexOptions.None));

    /// <summary>Convierte un comodín de ruta (<c>*</c>, <c>**</c>) en regex anclada.</summary>
    public static Regex ComodinARegex(string comodin)
    {
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < comodin.Length; i++)
        {
            var c = comodin[i];
            if (c == '*' && i + 1 < comodin.Length && comodin[i + 1] == '*')
            {
                sb.Append(".*");
                i++;
            }
            else if (c == '*')
            {
                sb.Append("[^/]*");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        return new Regex(sb.Append('$').ToString(), RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Aplica el vocabulario a un conjunto de textos <c>(fichero, clave o null, texto)</c> y devuelve cada
    /// aparición, marcada con la excepción que la cubre (si alguna). No decide el veredicto: eso lo hace el
    /// test contra la lista congelada.
    /// </summary>
    public static List<HallazgoDeVocabulario> Escanear(
        VocabularioJson vocabulario,
        IEnumerable<(string Fichero, string? Clave, string Texto)> textos)
    {
        var reglas = vocabulario.Prohibidos.Select(p => (p.Id, Regex: Compilar(p))).ToList();
        var excepciones = vocabulario.Excepciones
            .Select(e => (Excepcion: e, Ficheros: e.Ficheros.Select(ComodinARegex).ToList()))
            .ToList();
        var resultado = new List<HallazgoDeVocabulario>();

        foreach (var (fichero, clave, texto) in textos)
        {
            foreach (var (id, regex) in reglas)
            {
                foreach (Match m in regex.Matches(texto))
                {
                    var excepcion = excepciones.FirstOrDefault(e =>
                        e.Excepcion.Prohibidos.Contains(id)
                        && e.Ficheros.Any(f => f.IsMatch(fichero))
                        && (e.Excepcion.Claves is null || (clave is not null && e.Excepcion.Claves.Contains(clave))));

                    resultado.Add(new HallazgoDeVocabulario(fichero, clave, id, m.Value, excepcion.Excepcion?.Id));
                }
            }
        }

        return resultado;
    }

    /// <summary>
    /// Los valores de todo <c>.resx</c> de <c>src</c> (neutral y satélites <c>.ca-ES.resx</c>), uno por
    /// clave. Es lo que ve el usuario: a diferencia de los trinquetes de identificadores, aquí cuenta el
    /// texto.
    /// </summary>
    public static IEnumerable<(string Fichero, string? Clave, string Texto)> TextosDeResx() =>
        ArchivosDeSrc("*.resx").SelectMany(a => TextosDeResx(FuentesDeSrc.Relativa(a), File.ReadAllText(a)));

    public static IEnumerable<(string Fichero, string? Clave, string Texto)> TextosDeResx(string ruta, string contenido)
    {
        var raiz = XDocument.Parse(contenido).Root!;
        foreach (var dato in raiz.Elements("data"))
        {
            var valor = dato.Element("value")?.Value;
            var nombre = dato.Attribute("name")?.Value;
            if (!string.IsNullOrWhiteSpace(valor) && nombre is not null)
                yield return (ruta, nombre, valor);
        }
    }

    /// <summary>El texto de interfaz escrito a mano en cada <c>.razor</c> de <c>src</c>, uno por cadena distinta.</summary>
    public static IEnumerable<(string Fichero, string? Clave, string Texto)> TextosDeRazor() =>
        ArchivosDeSrc("*.razor").SelectMany(a => TextosDeRazor(FuentesDeSrc.Relativa(a), File.ReadAllText(a)));

    public static IEnumerable<(string Fichero, string? Clave, string Texto)> TextosDeRazor(string ruta, string contenido) =>
        DetectorTextosSinLocalizar.MedirRazor(contenido).Textos().Select(t => (ruta, (string?)null, t));

    /// <summary>Cuenta las apariciones por ubicación, sin las cubiertas por una excepción.</summary>
    public static Dictionary<Ubicacion, int> DeudaMedida(IEnumerable<HallazgoDeVocabulario> hallazgos)
    {
        var resultado = new Dictionary<Ubicacion, int>();
        foreach (var h in hallazgos.Where(h => h.IdExcepcion is null))
            resultado[h.Ubicacion] = resultado.GetValueOrDefault(h.Ubicacion) + 1;

        return resultado;
    }

    private static IEnumerable<string> ArchivosDeSrc(string patron)
    {
        var raiz = Path.Combine(FuentesDeSrc.RaizDelRepositorio(), "src");
        var s = Path.DirectorySeparatorChar;

        return Directory
            .EnumerateFiles(raiz, patron, SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{s}obj{s}") && !a.Contains($"{s}bin{s}") && !a.Contains($"{s}Migrations{s}"))
            .OrderBy(a => a, StringComparer.Ordinal);
    }
}
