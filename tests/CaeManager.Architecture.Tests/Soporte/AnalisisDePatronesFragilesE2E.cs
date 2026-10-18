using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

/// <summary>Un patrón frágil localizado en un texto fuente: cuál es y en qué línea (1 = primera).</summary>
internal sealed record PatronFragil(string Simbolo, int Linea);

/// <summary>
/// Detector de los dos patrones de E2E que más veces expulsaron una PR de la cola de fusión (semana del 2026-10-11:
/// 21 de 30 expulsiones fueron E2E), sobre <b>árboles de sintaxis</b> como <see cref="AnalisisDeSuiteE2E"/>: un
/// comentario o una cadena que los nombren no cuentan.
///
/// <list type="bullet">
/// <item><description>
/// <see cref="FocoSoloPorClase"/>: una espera a la clase <c>fila-enfocada</c> (<c>Expect(…).ToHaveCountAsync(1)</c> o
/// <c>ToHaveClassAsync(…fila-enfocada…)</c>) tras la cual se pulsa una tecla sin haber esperado antes el foco real
/// (<c>ToBeFocusedAsync</c>). La clase llega con el parche del servidor y el foco del DOM un fotograma después
/// (<c>enfocarFilaActiva</c>, <c>atajos-lista.js</c>); con el foco aún en el control de origen la tecla se descarta (#1340,
/// #1345). También cuenta el ayudante cuyo nombre empieza por «Enfocar» y termina en la espera por clase: su llamador
/// pulsará la tecla justo después. El receptor puede ser un localizador literal o una variable local cuyo inicializador
/// nombre la clase.
/// </description></item>
/// <item><description>
/// <see cref="ClicEnGrupoPlegado"/>: una sentencia que localiza un enlace del menú lateral (<c>nav-item</c>) de un grupo
/// que nace plegado (<c>Administración</c>, <c>Plataforma</c>: <c>AbiertoPorDefecto: false</c> en
/// <c>CatalogoMenuLateral</c>) en un método que no llama a <c>AbrirGrupoDelMenuAsync</c> ni a <c>AbrirGrupoYClickAsync</c>.
/// Sus enlaces existen en el DOM pero no son visibles y Playwright espera 30 s a un elemento oculto (#1413).
/// </description></item>
/// </list>
///
/// <para>
/// <b>Contrato efectivo, más estrecho que el nombre.</b> Sintaxis, no semántica: los nombres se comparan por su texto.
/// <c>FocoSoloPorClase</c> no ve una espera cuyo localizador llegue como parámetro o campo (solo literales y variables
/// locales del mismo método), ni una tecla pulsada en otro método tras una espera que no esté en un ayudante «Enfocar…»;
/// solo mira las sentencias siguientes del mismo bloque. <c>ClicEnGrupoPlegado</c> solo conoce la lista de rutas y rótulos
/// de <see cref="HrefsDeGruposPlegados"/>/<see cref="RotulosDeGruposPlegados"/>: un grupo nuevo plegado por defecto hay que
/// añadirlo ahí; y un grupo abierto en un método distinto del que hace clic (p. ej. en un <c>InitializeAsync</c>) se
/// cuenta como fallo y se lista con su justificación.
/// </para>
/// </summary>
internal static class AnalisisDePatronesFragilesE2E
{
    public const string FocoSoloPorClase = "FocoSoloPorClase";
    public const string ClicEnGrupoPlegado = "ClicEnGrupoPlegado";

    private const string ClaseDeFoco = "fila-enfocada";

    private static readonly string[] AyudantesQueAbrenElGrupo = ["AbrirGrupoDelMenuAsync", "AbrirGrupoYClickAsync"];

    /// <summary>Rutas de los enlaces de los grupos con <c>AbiertoPorDefecto: false</c> en <c>CatalogoMenuLateral.Enlaces</c>.</summary>
    private static readonly Regex HrefsDeGruposPlegados = new(
        @"href=\\?'(usuarios|equipo|configuracion(/[a-z0-9-]+)*|auditoria|delegaciones|plataforma(/[a-z0-9-]+)*)\\?'",
        RegexOptions.CultureInvariant);

    /// <summary>Rótulos de esos mismos enlaces, para quien los localiza por texto (<c>HasText = "Delegaciones"</c>).</summary>
    private static readonly string[] RotulosDeGruposPlegados =
        ["Usuarios", "Equipo", "Configuración", "Auditoría", "Delegaciones", "Estado comercial", "Conectores CAE", "Administración de plataforma"];

    public static List<PatronFragil> Analizar(string texto)
    {
        var raiz = CSharpSyntaxTree.ParseText(texto).GetRoot();
        var patrones = new List<PatronFragil>();

        foreach (var sentencia in Sentencias(raiz))
        {
            if (EsEsperaDeFocoPorClase(sentencia) && SeUsaSinFocoReal(sentencia))
                patrones.Add(new PatronFragil(FocoSoloPorClase, LineaDe(sentencia)));

            if (EsClicEnGrupoPlegado(sentencia))
                patrones.Add(new PatronFragil(ClicEnGrupoPlegado, LineaDe(sentencia)));
        }

        return patrones;
    }

    /// <summary>Las sentencias de expresión y de declaración local que no están dentro de otra (una lambda con cuerpo de bloque no las duplica).</summary>
    private static IEnumerable<StatementSyntax> Sentencias(SyntaxNode raiz) =>
        raiz.DescendantNodes()
            .OfType<StatementSyntax>()
            .Where(s => s is ExpressionStatementSyntax or LocalDeclarationStatementSyntax
                        && !s.Ancestors().Any(a => a is ExpressionStatementSyntax or LocalDeclarationStatementSyntax));

    // ───────────── FocoSoloPorClase ─────────────

    private static bool EsEsperaDeFocoPorClase(StatementSyntax sentencia)
    {
        var variablesDeFoco = VariablesQueNombranLaClase(sentencia);

        foreach (var invocacion in sentencia.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocacion.Expression is not MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax receptor } acceso
                || NombreDe(receptor.Expression) != "Expect")
            {
                continue; // «.Not.ToHave…» tiene como receptor un acceso, no un Expect(…): una negación no espera el foco.
            }

            var aSecas = receptor.ArgumentList.Arguments.FirstOrDefault()?.Expression;
            var nombraLaClase = aSecas is not null
                                && (aSecas.ToString().Contains(ClaseDeFoco, StringComparison.Ordinal)
                                    || (aSecas is IdentifierNameSyntax id && variablesDeFoco.Contains(id.Identifier.ValueText)));

            switch (acceso.Name.Identifier.ValueText)
            {
                case "ToHaveCountAsync" when nombraLaClase && invocacion.ArgumentList.Arguments.FirstOrDefault()?.ToString() == "1":
                    return true;
                case "ToHaveClassAsync" when invocacion.ArgumentList.ToString().Contains(ClaseDeFoco, StringComparison.Ordinal):
                    return true;
            }
        }

        return false;
    }

    /// <summary>Las variables locales del método cuyo inicializador nombra <c>fila-enfocada</c> (<c>var enfocada = page.Locator("tbody tr.fila-enfocada");</c>).</summary>
    private static HashSet<string> VariablesQueNombranLaClase(StatementSyntax sentencia)
    {
        var ambito = sentencia.Ancestors().FirstOrDefault(a => a is MethodDeclarationSyntax or LocalFunctionStatementSyntax or ConstructorDeclarationSyntax)
                     ?? sentencia.SyntaxTree.GetRoot();

        return ambito.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Initializer?.Value.ToString().Contains(ClaseDeFoco, StringComparison.Ordinal) == true)
            .Select(v => v.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Tras la espera por clase, la siguiente señal gana: un <c>ToBeFocusedAsync</c> la protege; una tecla (<c>PressAsync</c>,
    /// <c>Keyboard.…</c>) sin foco confirmado antes la deja expuesta. Si el bloque termina sin ninguna de las dos, solo
    /// cuenta cuando el método es un ayudante «Enfocar…»: su llamador pulsa la tecla justo después.
    /// </summary>
    private static bool SeUsaSinFocoReal(StatementSyntax espera)
    {
        if (espera.Parent is BlockSyntax bloque)
        {
            // El foco ya confirmado justo antes (ToBeFocusedAsync y luego la clase, en ese orden) protege igual.
            var anterior = bloque.Statements.TakeWhile(s => s != espera).LastOrDefault();
            if (anterior?.ToString().Contains("ToBeFocusedAsync", StringComparison.Ordinal) == true)
                return false;

            foreach (var siguiente in bloque.Statements.SkipWhile(s => s != espera).Skip(1))
            {
                var texto = siguiente.ToString();
                if (texto.Contains("ToBeFocusedAsync", StringComparison.Ordinal))
                    return false;
                if (texto.Contains("PressAsync", StringComparison.Ordinal) || texto.Contains("Keyboard.", StringComparison.Ordinal))
                    return true;
            }
        }

        var metodo = espera.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        return metodo is not null && metodo.Identifier.ValueText.StartsWith("Enfocar", StringComparison.Ordinal);
    }

    // ───────────── ClicEnGrupoPlegado ─────────────

    private static bool EsClicEnGrupoPlegado(StatementSyntax sentencia)
    {
        // Un localizador, no una cadena suelta: «var s = "a.nav-item[href='usuarios']";» no pulsa nada.
        if (!sentencia.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => NombreDe(i.Expression) == "Locator"))
            return false;

        var literales = Literales(sentencia);
        if (!literales.Any(l => l.Contains("nav-item", StringComparison.Ordinal)))
            return false;

        var enlaceDeGrupoPlegado = literales.Any(l => HrefsDeGruposPlegados.IsMatch(l) || RotulosDeGruposPlegados.Contains(l, StringComparer.Ordinal));
        if (!enlaceDeGrupoPlegado)
            return false;

        var ambito = sentencia.Ancestors().FirstOrDefault(a => a is MethodDeclarationSyntax or LocalFunctionStatementSyntax or ConstructorDeclarationSyntax)
                     ?? sentencia.SyntaxTree.GetRoot();

        return !ambito.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Any(i => AyudantesQueAbrenElGrupo.Contains(NombreDe(i.Expression), StringComparer.Ordinal));
    }

    /// <summary>El texto de cada literal de cadena de la sentencia, también los trozos fijos de las interpoladas.</summary>
    private static List<string> Literales(SyntaxNode nodo) =>
        nodo.DescendantTokens()
            .Where(t => t.IsKind(SyntaxKind.StringLiteralToken)
                        || t.IsKind(SyntaxKind.InterpolatedStringTextToken)
                        || t.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
                        || t.IsKind(SyntaxKind.MultiLineRawStringLiteralToken))
            .Select(t => t.ValueText)
            .ToList();

    private static string? NombreDe(ExpressionSyntax expresion) => expresion switch
    {
        MemberAccessExpressionSyntax acceso => acceso.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax enlace => enlace.Name.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => null,
    };

    private static int LineaDe(SyntaxNode nodo) => nodo.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    /// <summary>Los patrones de un conjunto de fuentes como medida de <see cref="ListaCongelada"/>: <c>fichero :: patrón = n</c>.</summary>
    public static Dictionary<Ubicacion, int> Medir(IEnumerable<(string Ruta, string Texto)> fuentes) =>
        fuentes
            .SelectMany(f => Analizar(f.Texto).Select(p => new Ubicacion(f.Ruta, p.Simbolo)))
            .GroupBy(u => u)
            .ToDictionary(g => g.Key, g => g.Count());
}
