using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
/// que las completaciones legítimas («Bandeja de entrada») ya quedan fuera del patrón. <see cref="Casa"/> y
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
    IReadOnlyList<string>? DescartarAntesDeCasar,
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
/// <b>Qué ve.</b> Tres orígenes. (1) El valor de cada <c>&lt;data&gt;&lt;value&gt;</c> de todo <c>.resx</c> de
/// <c>src</c>, neutral y satélites es/ca. (2) De cada <c>.razor</c>, el texto de interfaz escrito a mano en el
/// marcado: el mismo detector que <c>TextosSinLocalizarCongeladosTests</c> (texto entre etiquetas, atributos de
/// una lista cerrada y literales C# que parecen lenguaje natural) más los atributos cuyo NOMBRE dice que llevan
/// texto (<c>PlaceholderBuscador</c>…). (3) De todo el <c>.cs</c> de <c>src</c> (Application, Infrastructure, Web),
/// los mensajes de <c>Error.Crear(codigo, mensaje)</c> y de <c>.WithMessage(mensaje)</c>, que acaban en pantalla.
/// </para>
///
/// <para>
/// <b>Qué no ve, declarado.</b> Texto que llega de datos (catálogos sembrados); mensajes que el código monta
/// fuera de esas dos llamadas: un envoltorio privado que recibe el literal y llama a <c>Error.Crear(codigo,
/// mensaje)</c> (<c>Fallo("X.Y", "…")</c>), una constante de Domain, una excepción, <c>Result.Fallo</c> con un
/// literal suelto; otros literales <c>.cs</c> de Web e Infrastructure; texto montado en JavaScript; un atributo
/// cuyo valor lleva una expresión Razor (<c>@…</c>); el texto del bloque <c>@code</c> que el detector de lenguaje
/// natural no reconoce. Los literales de un mismo argumento (concatenación, interpolación) se unen con un espacio
/// antes de casar. Los
/// textos de un mismo <c>.razor</c> se cuentan por cadena distinta: retirar un «Tenant» y añadir otro con una
/// frase diferente deja el recuento igual (límite heredado del detector). Las <b>URL</b> se descartan antes de
/// casar (<c>descartarAntesDeCasar</c>), y los patrones de <c>Vocabulario.json</c> solo conocen castellano y
/// catalán.
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

    /// <summary>
    /// Patrones de lo que NO es lenguaje de pantalla aunque viva en un texto (una URL de ejemplo con
    /// <c>portal-del-operador.com</c>): se sustituyen por un espacio antes de casar. Es mejor descartar el
    /// objeto que no es texto que abrir un hueco en el patrón del término, que dejaría pasar compuestos reales
    /// como «empresa-operador».
    /// </summary>
    public static List<Regex> Descartes(VocabularioJson vocabulario) =>
        (vocabulario.DescartarAntesDeCasar ?? []).Select(p => new Regex(p, RegexOptions.CultureInvariant)).ToList();

    public static string Descartar(IEnumerable<Regex> descartes, string texto) =>
        descartes.Aggregate(texto, (t, d) => d.Replace(t, " "));

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
        var descartes = Descartes(vocabulario);
        var excepciones = vocabulario.Excepciones
            .Select(e => (Excepcion: e, Ficheros: e.Ficheros.Select(ComodinARegex).ToList()))
            .ToList();
        var resultado = new List<HallazgoDeVocabulario>();

        foreach (var (fichero, clave, textoCrudo) in textos)
        {
            var texto = Descartar(descartes, textoCrudo);
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
        DetectorTextosSinLocalizar.MedirRazor(contenido).Textos()
            .Concat(AtributosDeTexto(contenido))
            .Distinct(StringComparer.Ordinal)
            .Select(t => (ruta, (string?)null, t));

    /// <summary>
    /// Atributos del marcado cuyo NOMBRE dice que llevan texto para el usuario (<c>PlaceholderBuscador</c>,
    /// <c>EtiquetaCampo</c>, <c>aria-label</c>…), por contener una de esas palabras. El detector de texto sin
    /// localizar solo conoce una lista cerrada de nombres; un componente nuevo con otro nombre se le escaparía.
    /// Se queda con los valores sin expresiones Razor (<c>@…</c>) y ve solo el marcado, no el bloque
    /// <c>@code</c>. Quedan fuera las referencias a un <c>id</c> (<c>aria-labelledby</c>…), que no son texto.
    /// </summary>
    private static IEnumerable<string> AtributosDeTexto(string contenido)
    {
        var limpio = LimpiadorDeComentarios.Quitar(contenido.Replace("\r\n", "\n"), razor: true);
        var codigo = limpio.IndexOf("@code", StringComparison.Ordinal);
        var marcado = codigo >= 0 ? limpio[..codigo] : limpio;

        foreach (Match m in AtributoConTexto.Matches(marcado))
        {
            var valor = m.Groups["v"].Value.Trim();
            if (valor.Length > 0)
                yield return valor;
        }
    }

    private static readonly Regex AtributoConTexto = new(
        @"(?<![\w\-:@])(?!aria-(?:labelledby|describedby|controls|owns|activedescendant)\b)[\w-]*(?:Placeholder|Etiqueta|Titulo|Texto|valuetext|Mensaje|Descripcion|Description|Label|Title|Tooltip|Leyenda|Pista|Marcador|Ayuda|Kicker|Subtitulo|Explicacion|Aviso|Cabecera|Rotulo|alt)[\w-]*" +
        @"\s*=\s*""(?<v>[^""@]*)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Los mensajes de error y de validación que el código de <c>src</c> escribe a mano y que el usuario acaba
    /// viendo: el segundo argumento de <c>Error.Crear(codigo, mensaje)</c> (la «clave» es el código) y el argumento
    /// de <c>.WithMessage(mensaje)</c> (clave <c>WithMessage</c>). Un texto por argumento: los literales de cadena
    /// del argumento, también los trozos de una interpolación o de una concatenación, unidos con un espacio.
    /// </summary>
    public static IEnumerable<(string Fichero, string? Clave, string Texto)> TextosDeMensajes() =>
        ArchivosDeSrc("*.cs")
            .AsParallel().AsOrdered()
            .SelectMany(a => TextosDeMensajes(FuentesDeSrc.Relativa(a), File.ReadAllText(a)))
            .ToList();

    public static IEnumerable<(string Fichero, string? Clave, string Texto)> TextosDeMensajes(string ruta, string contenido)
    {
        var raiz = CSharpSyntaxTree.ParseText(contenido).GetRoot();

        foreach (var llamada in raiz.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var (dueno, nombre) = llamada.Expression switch
            {
                // «Error.Crear» y también «Domain.Common.Error.Crear»: el dueño es el último identificador de la cadena.
                MemberAccessExpressionSyntax acceso => (UltimoIdentificador(acceso.Expression), acceso.Name.Identifier.ValueText),
                _ => ((string?)null, string.Empty),
            };
            var argumentos = llamada.ArgumentList.Arguments;

            if (dueno == "Error" && nombre == "Crear" && argumentos.Count >= 2)
            {
                var codigo = argumentos[0].Expression is LiteralExpressionSyntax l ? l.Token.ValueText : "(código dinámico)";
                var texto = LiteralesDe(argumentos[1].Expression);
                if (texto.Length > 0)
                    yield return (ruta, codigo, texto);
            }
            else if (nombre == "WithMessage" && argumentos.Count >= 1)
            {
                var texto = LiteralesDe(argumentos[0].Expression);
                if (texto.Length > 0)
                    yield return (ruta, "WithMessage", texto);
            }
        }
    }

    private static string? UltimoIdentificador(ExpressionSyntax e) => e switch
    {
        IdentifierNameSyntax identificador => identificador.Identifier.ValueText,
        MemberAccessExpressionSyntax acceso => acceso.Name.Identifier.ValueText,
        _ => null,
    };

    private static string LiteralesDe(SyntaxNode expresion) =>
        string.Join(' ', expresion.DescendantTokens()
            .Where(t => t.Kind() is SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken
                or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken)
            .Select(t => t.ValueText)
            .Where(t => !string.IsNullOrWhiteSpace(t)));

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
