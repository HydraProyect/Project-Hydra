using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Localiza las cadenas de C# de un <c>.razor</c> ya sin comentarios (issue #2113). Un .razor no dice
/// dónde acaba el marcado y empieza el C#, así que no se puede parsear entero; en cambio cada
/// cadena empieza en un carácter reconocible (<c>"</c>, con o sin <c>$</c> y <c>@</c> delante) y su
/// final lo decide el léxico de Roslyn, que es la gramática de referencia: lee la normal, la
/// verbatim, la cruda de una o varias líneas, la interpolada con huecos que contienen otras cadenas
/// (<c>$"{Ejecutar("BEGIN;")}"</c>) y todo salto de línea de C# (también CR solo).
///
/// <para>
/// Lo que Roslyn no puede saber es que unas comillas del marcado no son C#. Tres defensas:
/// (1) un literal de carácter (<c>'"'</c>) se salta con una gramática propia estricta antes de
/// mirar nada, para que su comilla no abra cadena; un apóstrofo suelto del texto no cumple esa
/// gramática y se ignora; (2) un token con errores léxicos (la apertura de una cruda sin cierre,
/// una cadena normal sin cerrar en la línea) no es cadena y se sigue en el carácter siguiente;
/// (3) el interior de una cruda multilínea se vuelve a escanear, porque unas comillas del marcado
/// con forma de apertura pueden emparejarse con el cierre de una cruda real posterior y dejar la
/// apertura real, con su contenido, dentro de un token ajeno.
/// </para>
/// </summary>
internal static class EscanerDeCadenasEnRazor
{
    /// <param name="Inicio">Posición del primer carácter (el <c>$</c> o <c>@</c> del prefijo, si lo hay).</param>
    /// <param name="Longitud">Longitud del literal, con prefijo y delimitadores.</param>
    /// <param name="EsDeInterior">Salió de volver a escanear el interior de una cruda multilínea, no del recorrido principal.</param>
    internal readonly record struct Cadena(int Inicio, int Longitud, bool EsDeInterior);

    private const int ProfundidadMaxima = 3;

    private static readonly CSharpParseOptions Opciones = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    /// <summary>
    /// Un literal de carácter: <c>'x'</c>, <c>'\n'</c>, <c>'\''</c>, <c>'A'</c>, <c>'\x41'</c>. Estricto: lo que
    /// no encaja exactamente (un apóstrofo del texto) no es un literal de carácter.
    /// </summary>
    private static readonly Regex LiteralDeCaracter = new(
        @"\G'(?:[^'\\\r\n]|\\(?:u[0-9a-fA-F]{4}|U[0-9a-fA-F]{8}|x[0-9a-fA-F]{1,4}|[^\r\n]))'",
        RegexOptions.Compiled);

    public static IReadOnlyList<Cadena> Escanear(string texto)
    {
        var halladas = new Dictionary<int, Cadena>();
        Recorrer(texto, 0, texto.Length, esDeInterior: false, profundidad: 0, halladas);
        return [.. halladas.Values.OrderBy(c => c.Inicio)];
    }

    private static void Recorrer(string texto, int desde, int hasta, bool esDeInterior, int profundidad, Dictionary<int, Cadena> halladas)
    {
        var i = desde;
        while (i < hasta)
        {
            if (texto[i] == '\'')
            {
                var caracter = LiteralDeCaracter.Match(texto, i);
                i += caracter.Success ? caracter.Length : 1;
                continue;
            }

            if (!PuedeAbrirCadena(texto, i) || LeerToken(texto, i) is not { } token)
            {
                i++;
                continue;
            }

            // El recorrido principal manda: una cadena que también salió de un interior se cuenta como principal.
            if (!halladas.TryGetValue(i, out var previa) || (previa.EsDeInterior && !esDeInterior))
                halladas[i] = new Cadena(i, token.Longitud, esDeInterior);

            if (token.EsCrudaMultilinea && profundidad < ProfundidadMaxima)
            {
                var apertura = i;
                while (texto[apertura] == '$') apertura++;
                while (texto[apertura] == '"') apertura++;
                var cierre = i + token.Longitud;
                while (cierre > apertura && texto[cierre - 1] == '"') cierre--;
                if (cierre > apertura)
                    Recorrer(texto, apertura, cierre, esDeInterior: true, profundidad + 1, halladas);
            }

            i += token.Longitud;
        }
    }

    private static bool PuedeAbrirCadena(string texto, int i)
    {
        var j = i;
        while (j < texto.Length && (texto[j] == '$' || texto[j] == '@')) j++;
        return j < texto.Length && texto[j] == '"';
    }

    /// <summary>El primer token de Roslyn desde <paramref name="i"/>, si es una cadena léxicamente válida.</summary>
    private static (int Longitud, bool EsCrudaMultilinea)? LeerToken(string texto, int i)
    {
        var token = SyntaxFactory.ParseTokens(texto, i, i, Opciones).First();
        if (!EsDeCadena(token.Kind()) || token.Span.Length < 2)
            return null;
        if (token.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            return null;

        var literal = token.Text;
        if (string.CompareOrdinal(texto, i, literal, 0, literal.Length) != 0)
            return null; // el léxico no leyó donde le dijimos: no se da por buena

        var sinPrefijo = literal.TrimStart('$');
        return (literal.Length, sinPrefijo.StartsWith("\"\"\"", StringComparison.Ordinal) && literal.AsSpan().IndexOfAny(SaltosDeLinea) >= 0);
    }

    private static readonly System.Buffers.SearchValues<char> SaltosDeLinea = System.Buffers.SearchValues.Create("\r\n\u0085\u2028\u2029");

    private static bool EsDeCadena(SyntaxKind tipo) => tipo is
        SyntaxKind.StringLiteralToken
        or SyntaxKind.InterpolatedStringToken
        or SyntaxKind.Utf8StringLiteralToken
        or SyntaxKind.SingleLineRawStringLiteralToken
        or SyntaxKind.MultiLineRawStringLiteralToken
        or SyntaxKind.Utf8SingleLineRawStringLiteralToken
        or SyntaxKind.Utf8MultiLineRawStringLiteralToken;
}
