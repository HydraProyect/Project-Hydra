using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Lo que un fichero de <c>src</c> «dice» para un trinquete por ubicación: las palabras que lo
/// componen según su régimen (identificadores de C#, palabras de un <c>.razor</c> sin
/// comentarios, palabras de los <c>&lt;value&gt;</c> de un <c>.resx</c> neutral) y cuántas veces
/// compara <c>EsCritico</c> o <c>NivelServicio</c> con <c>null</c>. Solo en <c>.cs</c>,
/// <see cref="Literales"/> trae además las palabras de los literales de cadena, que
/// <see cref="Palabras"/> deja fuera a propósito: un trinquete de vocabulario visible (Bandeja) las
/// necesita; uno de identificadores (ClienteId) no.
/// </summary>
internal sealed record AnalisisDeFichero(
    IReadOnlyDictionary<string, int> Palabras,
    IReadOnlyDictionary<string, int> ComparacionesConNull,
    IReadOnlyDictionary<string, int>? Literales = null);

/// <summary>
/// Enumeración y análisis, hecho una sola vez por proceso de test, del código de <c>src</c> que
/// vigilan los trinquetes por ubicación (<see cref="ListaCongelada"/>).
///
/// <para>
/// <b>Tres regímenes, declarados.</b> <c>.cs</c>: solo cuentan los <c>IdentifierToken</c> del
/// árbol sintáctico de Roslyn; un comentario, un doc-comment o un literal de cadena no cuentan
/// (DEC-65 de <c>TerminologiaCanonicaTests</c>). <c>.razor</c>: palabras del texto una vez
/// quitados los comentarios (<c>@* … *@</c>, <c>&lt;!-- … --&gt;</c>, y los de C# de los
/// bloques de código) con <see cref="LimpiadorDeComentarios"/>; un <c>.razor</c> no se puede
/// parsear como C#, así que el marcado visible y las cadenas SÍ cuentan. <c>.resx</c>: solo el
/// neutral y solo el texto de cada <c>&lt;data&gt;&lt;value&gt;</c>.
/// </para>
///
/// <para>
/// <b>Qué no ve, y es deliberado.</b> Las migraciones (historia aplicada que no se edita), el
/// satélite <c>.ca-ES.resx</c> (nace como copia del neutral), un identificador dentro de un
/// literal de cadena (<c>HasColumnName("ClienteId")</c>, <c>nameof</c> sí cuenta porque es un
/// identificador) y <c>tests/</c>. Quien necesite ver una columna física con otro nombre de
/// propiedad usa el trinquete del modelo EF, que lee los metadatos y no el texto.
/// </para>
/// </summary>
internal static class FuentesDeSrc
{
    public static readonly string[] NombresDeDiscriminador = ["EsCritico", "NivelServicio"];

    private static readonly Lazy<IReadOnlyDictionary<string, AnalisisDeFichero>> Cache = new(Analizar);

    /// <summary>Ruta relativa a la raíz del repositorio, con <c>/</c>, → análisis del fichero.</summary>
    public static IReadOnlyDictionary<string, AnalisisDeFichero> Analisis => Cache.Value;

    public static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
        {
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
                " — este test necesita el árbol fuente del repositorio, no solo los ensamblados compilados.");
        }

        return actual.FullName;
    }

    public static string Relativa(string archivo) =>
        Path.GetRelativePath(RaizDelRepositorio(), archivo).Replace('\\', '/');

    /// <summary>
    /// <c>src/</c> menos migraciones, <c>obj/</c> y <c>bin/</c>: <c>.cs</c>, <c>.razor</c> y
    /// <c>.resx</c> neutrales.
    /// </summary>
    public static IEnumerable<string> Archivos()
    {
        var raiz = Path.Combine(RaizDelRepositorio(), "src");
        var s = Path.DirectorySeparatorChar;

        return Directory
            .EnumerateFiles(raiz, "*", SearchOption.AllDirectories)
            .Where(a => a.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || a.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                        || EsResxNeutral(a))
            .Where(a => !a.Contains($"{s}obj{s}") && !a.Contains($"{s}bin{s}") && !a.Contains($"{s}Migrations{s}"));
    }

    public static bool EsResxNeutral(string archivo)
    {
        if (!archivo.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
            return false;

        var satelite = Satelite.Match(archivo);
        return !(satelite.Success && File.Exists(satelite.Groups["base"].Value + ".resx"));
    }

    private static readonly Regex Satelite =
        new(@"^(?<base>.+)\.[a-z]{2,3}(-[A-Za-z0-9]{2,8})*\.resx$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static IReadOnlyDictionary<string, AnalisisDeFichero> Analizar() =>
        Archivos()
            .AsParallel()
            .Select(a => (Ruta: Relativa(a), Analisis: AnalizarArchivo(a)))
            .ToDictionary(p => p.Ruta, p => p.Analisis, StringComparer.Ordinal);

    private static AnalisisDeFichero AnalizarArchivo(string archivo)
    {
        var texto = File.ReadAllText(archivo);

        if (archivo.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            return AnalizarRazor(texto);

        if (archivo.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
            return AnalizarResx(texto);

        return AnalizarCSharp(texto);
    }

    // ───────────────────────── .cs ─────────────────────────

    public static AnalisisDeFichero AnalizarCSharp(string texto)
    {
        var raiz = CSharpSyntaxTree.ParseText(texto).GetRoot();
        var palabras = new Dictionary<string, int>(StringComparer.Ordinal);
        var comparaciones = new Dictionary<string, int>(StringComparer.Ordinal);
        var literales = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var token in raiz.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.IdentifierToken))
            {
                Sumar(palabras, token.ValueText);
            }
            else if (token.Kind() is SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken
                     or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken
                     or SyntaxKind.Utf8StringLiteralToken or SyntaxKind.Utf8SingleLineRawStringLiteralToken
                     or SyntaxKind.Utf8MultiLineRawStringLiteralToken)
            {
                foreach (Match m in Palabra.Matches(token.ValueText))
                    Sumar(literales, m.Value);
            }
        }

        foreach (var nodo in raiz.DescendantNodes())
        {
            var nombre = NombreComparadoConNull(nodo);
            if (nombre is not null)
                Sumar(comparaciones, nombre);
        }

        return new AnalisisDeFichero(palabras, comparaciones, literales);
    }

    /// <summary>
    /// Si <paramref name="nodo"/> decide algo por la nulidad de <c>EsCritico</c> o
    /// <c>NivelServicio</c>, devuelve cuál. Formas: <c>x.Nombre != null</c> / <c>== null</c>
    /// (en cualquiera de los dos lados), <c>x.Nombre is null</c>, <c>is not null</c>,
    /// <c>is { }</c>, <c>is not { }</c>, patrones de propiedades o de tipo (<c>is bool b</c>) y
    /// <c>x.Nombre.HasValue</c>; <c>== default</c>; patrones compuestos (<c>is null or ""</c>,
    /// <c>is true or false</c>); y <c>string.IsNullOrEmpty/IsNullOrWhiteSpace(x.Nombre)</c>.
    /// Ciego (declarado, no cubierto por prueba que lo vea): <c>switch</c> con brazo <c>null</c>,
    /// <c>??</c>, la nulidad leída a través de una variable intermedia
    /// (<c>var c = e.EsCritico; if (c is null)</c>), <c>string.Equals</c>/<c>ReferenceEquals</c> y
    /// cualquier llamada que reciba el valor y devuelva si es nulo (un método propio).
    /// </summary>
    private static string? NombreComparadoConNull(SyntaxNode nodo)
    {
        switch (nodo)
        {
            // «x is bool» (sin designación) lo parsea Roslyn como expresión binaria de tipo, no como patrón.
            case BinaryExpressionSyntax prueba when prueba.IsKind(SyntaxKind.IsExpression):
                return NombreDeDiscriminador(prueba.Left);

            case BinaryExpressionSyntax binaria
                when binaria.IsKind(SyntaxKind.EqualsExpression) || binaria.IsKind(SyntaxKind.NotEqualsExpression):
                if (EsNulo(binaria.Right))
                    return NombreDeDiscriminador(binaria.Left);
                if (EsNulo(binaria.Left))
                    return NombreDeDiscriminador(binaria.Right);
                return null;

            case IsPatternExpressionSyntax esPatron when PatronDeNulidad(esPatron.Pattern):
                return NombreDeDiscriminador(esPatron.Expression);

            case MemberAccessExpressionSyntax acceso when acceso.Name.Identifier.ValueText == "HasValue":
                return NombreDeDiscriminador(acceso.Expression);

            // string.IsNullOrEmpty(x.NivelServicio) / IsNullOrWhiteSpace: la misma pregunta para un string?.
            case InvocationExpressionSyntax llamada
                when NombreInvocado(llamada.Expression) is "IsNullOrEmpty" or "IsNullOrWhiteSpace"
                     && llamada.ArgumentList.Arguments.Count == 1:
                return NombreDeDiscriminador(llamada.ArgumentList.Arguments[0].Expression);

            default:
                return null;
        }
    }

    private static string? NombreInvocado(ExpressionSyntax e) => e switch
    {
        MemberAccessExpressionSyntax acceso => acceso.Name.Identifier.ValueText,
        IdentifierNameSyntax identificador => identificador.Identifier.ValueText,
        _ => null,
    };

    private static bool EsNulo(ExpressionSyntax e)
    {
        while (e is ParenthesizedExpressionSyntax p)
            e = p.Expression;

        // «== default» sobre un anulable es «== null».
        return e.IsKind(SyntaxKind.NullLiteralExpression) || e.IsKind(SyntaxKind.DefaultLiteralExpression)
               || e.IsKind(SyntaxKind.DefaultExpression);
    }

    /// <summary>
    /// Un patrón que solo se cumple (o solo falla) según la nulidad: <c>null</c>, <c>not null</c>,
    /// <c>{ }</c>, un patrón de propiedades (<c>{ Value: true }</c>) y un patrón de tipo
    /// (<c>is bool</c>, <c>is bool b</c>), que son la misma pregunta con otra forma. Una constante
    /// (<c>is true</c>) no: pregunta por el valor, no por la presencia.
    /// </summary>
    private static bool PatronDeNulidad(PatternSyntax patron) => patron switch
    {
        ConstantPatternSyntax constante => EsNulo(constante.Expression),
        UnaryPatternSyntax negado when negado.IsKind(SyntaxKind.NotPattern) => PatronDeNulidad(negado.Pattern),
        RecursivePatternSyntax => true,
        DeclarationPatternSyntax => true,
        TypePatternSyntax => true,
        ParenthesizedPatternSyntax parentesis => PatronDeNulidad(parentesis.Pattern),
        // «is null or ""», «is not (null or "")»: basta con que una rama pregunte por la nulidad.
        // «is true or false» abarca todo valor no nulo de un bool?: es la misma pregunta.
        BinaryPatternSyntax binario => PatronDeNulidad(binario.Left) || PatronDeNulidad(binario.Right) || EsTrueOFalse(binario),
        _ => false,
    };

    private static bool EsTrueOFalse(BinaryPatternSyntax binario) =>
        binario.IsKind(SyntaxKind.OrPattern)
        && binario.DescendantNodes().OfType<ConstantPatternSyntax>().Select(c => c.Expression.Kind()).Distinct()
            .Count(k => k is SyntaxKind.TrueLiteralExpression or SyntaxKind.FalseLiteralExpression) == 2;

    private static string? NombreDeDiscriminador(ExpressionSyntax e)
    {
        var nombre = e switch
        {
            IdentifierNameSyntax identificador => identificador.Identifier.ValueText,
            MemberAccessExpressionSyntax acceso => acceso.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax enlace => enlace.Name.Identifier.ValueText,
            ConditionalAccessExpressionSyntax condicional => NombreDeDiscriminador(condicional.WhenNotNull),
            ParenthesizedExpressionSyntax parentesis => NombreDeDiscriminador(parentesis.Expression),
            PostfixUnaryExpressionSyntax posfijo when posfijo.IsKind(SyntaxKind.SuppressNullableWarningExpression) =>
                NombreDeDiscriminador(posfijo.Operand),
            _ => null,
        };

        return nombre is not null && NombresDeDiscriminador.Contains(nombre) ? nombre : null;
    }

    // ───────────────────────── .razor ─────────────────────────

    private static readonly Regex Palabra = new(@"[\p{L}_][\p{L}\p{N}_]*", RegexOptions.Compiled);

    // El equivalente de las formas que el analizador de C# reconoce, sobre texto: un .razor no se parsea
    // como C#, así que esto es una aproximación y está declarada como tal (sin patrones compuestos
    // más allá de «is (», sin variables intermedias).
    private static readonly Regex ComparacionEnRazor = new(
        @"\b(?<n>EsCritico|NivelServicio)\b\s*(?:!=|==)\s*(?:null|default)\b"
        + @"|\b(?:null|default)\s*(?:!=|==)\s*(?:[\w.]*\.)?(?<n>EsCritico|NivelServicio)\b"
        + @"|\b(?<n>EsCritico|NivelServicio)\s+is\s+(?:not\s+)?(?:null\b|\{|bool\b|string\b|\(|true\s+or\s+false\b)"
        + @"|\b(?<n>EsCritico|NivelServicio)\.HasValue\b"
        + @"|IsNullOr(?:Empty|WhiteSpace)\(\s*[\w.?!]*?\b(?<n>EsCritico|NivelServicio)\b\s*\)",
        RegexOptions.Compiled);

    public static AnalisisDeFichero AnalizarRazor(string texto)
    {
        var sinComentarios = LimpiadorDeComentarios.Quitar(texto, razor: true);
        var comparaciones = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Match m in ComparacionEnRazor.Matches(sinComentarios))
            Sumar(comparaciones, m.Groups["n"].Value);

        return new AnalisisDeFichero(PalabrasDe(sinComentarios), comparaciones);
    }

    // ───────────────────────── .resx ─────────────────────────

    public static AnalisisDeFichero AnalizarResx(string texto)
    {
        var valores = string.Join('\n', XDocument.Parse(texto).Root!
            .Elements("data").Elements("value").Select(v => v.Value));

        return new AnalisisDeFichero(PalabrasDe(valores), new Dictionary<string, int>());
    }

    private static Dictionary<string, int> PalabrasDe(string texto)
    {
        var palabras = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in Palabra.Matches(texto))
            Sumar(palabras, m.Value);

        return palabras;
    }

    private static void Sumar(Dictionary<string, int> cuenta, string clave) =>
        cuenta[clave] = cuenta.GetValueOrDefault(clave) + 1;

    // ───────────────────────── consultas ─────────────────────────

    /// <summary>
    /// Ubicaciones <c>(fichero, palabra)</c> cuya palabra cumple <paramref name="esLegacy"/>,
    /// con su número de apariciones.
    /// </summary>
    public static Dictionary<Ubicacion, int> UbicacionesDePalabras(
        IReadOnlyDictionary<string, AnalisisDeFichero> analisis,
        Func<string, bool> esLegacy,
        bool incluirLiterales = false)
    {
        var resultado = new Dictionary<Ubicacion, int>();

        foreach (var (ruta, fichero) in analisis)
        {
            foreach (var (palabra, n) in fichero.Palabras)
            {
                if (esLegacy(palabra))
                    resultado[new Ubicacion(ruta, palabra)] = n;
            }

            if (!incluirLiterales || fichero.Literales is null)
                continue;

            foreach (var (palabra, n) in fichero.Literales)
            {
                if (!esLegacy(palabra))
                    continue;

                var ubicacion = new Ubicacion(ruta, palabra);
                resultado[ubicacion] = resultado.GetValueOrDefault(ubicacion) + n;
            }
        }

        return resultado;
    }

    public static Dictionary<Ubicacion, int> UbicacionesDeComparacionesConNull(
        IReadOnlyDictionary<string, AnalisisDeFichero> analisis)
    {
        var resultado = new Dictionary<Ubicacion, int>();

        foreach (var (ruta, fichero) in analisis)
        {
            foreach (var (nombre, n) in fichero.ComparacionesConNull)
                resultado[new Ubicacion(ruta, nombre)] = n;
        }

        return resultado;
    }
}
