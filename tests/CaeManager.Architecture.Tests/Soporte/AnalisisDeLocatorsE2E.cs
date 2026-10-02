using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Cuenta, por fichero de <c>tests/CaeManager.E2ETests</c>, los localizadores de Playwright que
/// dependen de <b>cómo se ve</b> la pantalla y no de lo que es (S6 del análisis de causas raíz de
/// 2026-10-02, clase C10): cambiar un rótulo o reordenar una lista rompe el E2E aunque el
/// comportamiento sea el mismo, y tres hallazgos ALTA del revisor-puente del lote UX fueron
/// exactamente eso.
///
/// <list type="bullet">
/// <item><c>GetByText</c>: llamadas a <c>GetByText(…)</c>.</item>
/// <item><c>Posicional</c>: <c>.First</c> y <c>.Last</c> usados como propiedad (el
/// <c>First()</c> de LINQ lleva paréntesis y no cuenta), <c>.Nth(…)</c> y selectores de texto
/// con <c>:nth-…</c>, <c>:first…</c>, <c>:last…</c> o <c>&gt;&gt; nth=</c>.</item>
/// <item><c>LocatorPorTexto</c>: selectores CSS de Playwright que buscan por texto
/// (<c>text=</c>, <c>:has-text(</c>, <c>:text(</c>, <c>:text-is(</c>), que es <c>GetByText</c> por
/// otro camino. No lo pide el documento: sin él, el trinquete se esquiva con
/// <c>Locator("button:has-text('X')")</c>. Solo cuenta una cadena pasada como argumento de una
/// llamada, no una asignada a una variable o a un <c>const</c>.</item>
/// <item><c>FiltroPorTexto</c>: <c>HasText</c> y <c>HasNotText</c> (<c>Locator.Filter</c> y el
/// parámetro de <c>Locator</c>), la tercera forma de localizar por texto visible. Tampoco lo pide
/// el documento y por la misma razón.</item>
/// </list>
///
/// <para>
/// <b>Qué NO congela</b> y es deliberado: <c>GetByLabel</c>, <c>GetByPlaceholder</c> y
/// <c>GetByRole(name:)</c> también dependen de un rótulo, pero el documento los trata aparte
/// (el rótulo es el del recurso y es el contrato de accesibilidad), y la salida correcta es
/// <c>data-testid</c> en los componentes de Web, que es trabajo de S2a/S12 y no de este
/// instrumento. El 2026-10-02 había 0 <c>GetByTestId</c>.
/// </para>
/// </summary>
internal static class AnalisisDeLocatorsE2E
{
    public const string SimboloGetByText = "GetByText";
    public const string SimboloPosicional = "Posicional";
    public const string SimboloPorTexto = "LocatorPorTexto";
    public const string SimboloFiltroPorTexto = "FiltroPorTexto";

    private static readonly Regex SelectorPosicional = new(
        @":nth-[a-z-]+|:first\b|:last\b|:first-[a-z-]+|:last-[a-z-]+|>>\s*nth=",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SelectorPorTexto = new(
        @"(?<![\w-])text=|:has-text\(|:text\(|:text-is\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static Dictionary<string, int> Analizar(string texto)
    {
        var raiz = CSharpSyntaxTree.ParseText(texto).GetRoot();
        var cuenta = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var nodo in raiz.DescendantNodes())
        {
            switch (nodo)
            {
                case InvocationExpressionSyntax invocacion:
                    var nombre = NombreInvocado(invocacion.Expression);
                    if (nombre == "GetByText")
                        Sumar(cuenta, SimboloGetByText);
                    else if (nombre == "Nth")
                        Sumar(cuenta, SimboloPosicional);
                    break;

                case MemberAccessExpressionSyntax acceso
                    when acceso.Name.Identifier.ValueText is "First" or "Last"
                         && !(acceso.Parent is InvocationExpressionSyntax padre && padre.Expression == acceso):
                    Sumar(cuenta, SimboloPosicional);
                    break;

                case LiteralExpressionSyntax literal
                    when literal.IsKind(SyntaxKind.StringLiteralExpression) && EsArgumentoDeInvocacion(literal):
                    ContarLiteral(cuenta, literal.Token.ValueText);
                    break;

                case InterpolatedStringExpressionSyntax interpolada when EsArgumentoDeInvocacion(interpolada):
                    foreach (var parte in interpolada.Contents.OfType<InterpolatedStringTextSyntax>())
                        ContarLiteral(cuenta, parte.TextToken.ValueText);
                    break;

                case IdentifierNameSyntax { Identifier.ValueText: "HasText" or "HasNotText" }:
                    Sumar(cuenta, SimboloFiltroPorTexto);
                    break;
            }
        }

        return cuenta;
    }

    /// <summary>
    /// Un selector solo es un selector si se pasa a una llamada (<c>page.Locator("…")</c>,
    /// <c>ClickAsync("…")</c>): una cadena que se asigna a una variable o a un <c>const</c> no se cuenta
    /// (un falso positivo con «text=» dentro de una URL o de un mensaje de aserción), a costa de no
    /// ver un selector que primero se guarda en una variable y luego se pasa.
    /// </summary>
    private static bool EsArgumentoDeInvocacion(ExpressionSyntax literal) =>
        literal.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax } };

    private static void ContarLiteral(Dictionary<string, int> cuenta, string valor)
    {
        for (var i = 0; i < SelectorPosicional.Matches(valor).Count; i++)
            Sumar(cuenta, SimboloPosicional);

        for (var i = 0; i < SelectorPorTexto.Matches(valor).Count; i++)
            Sumar(cuenta, SimboloPorTexto);
    }

    private static string? NombreInvocado(ExpressionSyntax e) => e switch
    {
        MemberAccessExpressionSyntax acceso => acceso.Name.Identifier.ValueText,
        IdentifierNameSyntax identificador => identificador.Identifier.ValueText,
        _ => null,
    };

    private static void Sumar(Dictionary<string, int> cuenta, string clave) =>
        cuenta[clave] = cuenta.GetValueOrDefault(clave) + 1;

    public static Dictionary<Ubicacion, int> MedirE2E()
    {
        var carpeta = Path.Combine(FuentesDeSrc.RaizDelRepositorio(), "tests", "CaeManager.E2ETests");
        var s = Path.DirectorySeparatorChar;
        var resultado = new Dictionary<Ubicacion, int>();

        var ficheros = Directory.EnumerateFiles(carpeta, "*.cs", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{s}obj{s}") && !a.Contains($"{s}bin{s}"));

        foreach (var archivo in ficheros)
        {
            var ruta = FuentesDeSrc.Relativa(archivo);
            foreach (var (simbolo, n) in Analizar(File.ReadAllText(archivo)))
                resultado[new Ubicacion(ruta, simbolo)] = n;
        }

        return resultado;
    }
}
