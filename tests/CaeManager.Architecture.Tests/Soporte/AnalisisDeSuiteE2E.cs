using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

/// <summary>Una espera fija localizada en un texto fuente: qué llamada es y en qué línea (1 = primera).</summary>
internal sealed record EsperaFija(string Simbolo, int Linea);

/// <summary>
/// Cómo recibe su colección una clase de test, <b>leído en el texto de la propia declaración</b>:
/// <c>Literal</c> es exactamente <c>[Collection("Nombre")]</c> en una lista de atributos propia;
/// <c>NoLiteral</c> es cualquier otra forma de escribirlo (constante, <c>nameof</c>, cadena con escapes o
/// literal <c>@"…"</c>, argumento con nombre, <c>CollectionAttribute</c>, atributo compartiendo corchetes con
/// otro, dos atributos <c>Collection</c>); <c>SinAtributo</c> es que la declaración no lleva ninguno.
/// </summary>
internal enum ViaDeColeccion
{
    Literal,
    NoLiteral,
    SinAtributo,
}

/// <summary>
/// Una declaración de clase que ejecuta tests. <paramref name="Coleccion"/> es el nombre si la vía es
/// <see cref="ViaDeColeccion.Literal"/>, el texto del atributo tal como está escrito si es
/// <see cref="ViaDeColeccion.NoLiteral"/> y <c>null</c> si no hay atributo. <paramref name="HeredaDe"/> nombra la
/// clase base del mismo árbol de la que recibe tests (<c>null</c> si no hereda ninguno).
/// </summary>
internal sealed record ClaseDeTest(
    string Ruta,
    string Nombre,
    int Linea,
    ViaDeColeccion Via,
    string? Coleccion,
    int TestsPropios,
    string? HeredaDe);

/// <summary>
/// Una <c>[CollectionDefinition]</c>. <paramref name="Nombre"/> es <c>null</c> cuando el argumento no es un
/// literal de cadena llano; entonces <paramref name="Texto"/> conserva lo escrito para el mensaje.
/// </summary>
internal sealed record DefinicionDeColeccion(string Ruta, string Clase, int Linea, string? Nombre, string Texto);

internal sealed record ColeccionesDeSuite(
    IReadOnlyList<DefinicionDeColeccion> Definiciones,
    IReadOnlyList<ClaseDeTest> Clases,
    IReadOnlyList<string> AtributosDeTest);

/// <summary>
/// Detectores de las dos guardas de la suite E2E (<c>EsperasFijasDeE2ECongeladasTests</c> y
/// <c>ColeccionesDeE2ECongeladasTests</c>), sobre <b>árboles de sintaxis</b> y no sobre expresiones regulares:
/// un comentario, un <c>&lt;c&gt;</c> de documentación o una cadena que contengan <c>Task.Delay(</c> o
/// <c>[Collection("…")]</c> no son código y no cuentan.
///
/// <para>
/// <b>Sintaxis, no semántica.</b> No hay compilación: los nombres se comparan por su texto. Por eso el contrato
/// efectivo es más estrecho que el nombre, y cada hueco está escrito en la guarda que lo sufre.
/// </para>
/// </summary>
internal static class AnalisisDeSuiteE2E
{
    public const string EsperaDePlaywright = "WaitForTimeoutAsync";
    public const string EsperaDeTarea = "Task.Delay";
    public const string EsperaDeHilo = "Thread.Sleep";

    /// <summary>Atributos de xUnit (y de Xunit.SkippableFact) que convierten un método en test.</summary>
    private static readonly string[] AtributosDeTestDeXunit = ["Fact", "Theory", "SkippableFact", "SkippableTheory"];

    /// <summary>
    /// Las esperas fijas de un texto fuente: toda invocación cuyo nombre de miembro sea <c>WaitForTimeoutAsync</c>
    /// (sea cual sea el receptor: <c>page</c>, <c>Page</c>, <c>pagina?.</c>…), y <c>Task.Delay</c> o
    /// <c>Thread.Sleep</c> con el tipo escrito, también cualificado (<c>System.Threading.Tasks.Task.Delay</c>).
    /// </summary>
    public static List<EsperaFija> EsperasFijas(string texto)
    {
        var esperas = new List<EsperaFija>();

        foreach (var invocacion in CSharpSyntaxTree.ParseText(texto).GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var simbolo = SimboloDeEspera(invocacion.Expression);
            if (simbolo is not null)
                esperas.Add(new EsperaFija(simbolo, LineaDe(invocacion)));
        }

        return esperas;
    }

    private static string? SimboloDeEspera(ExpressionSyntax expresion)
    {
        var nombre = expresion switch
        {
            MemberAccessExpressionSyntax acceso => acceso.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax enlace => enlace.Name.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => null,
        };

        if (nombre == EsperaDePlaywright)
            return EsperaDePlaywright;

        if (expresion is not MemberAccessExpressionSyntax conTipo)
            return null;

        return (UltimoNombre(conTipo.Expression), nombre) switch
        {
            ("Task", "Delay") => EsperaDeTarea,
            ("Thread", "Sleep") => EsperaDeHilo,
            _ => null,
        };
    }

    /// <summary>El identificador más a la derecha de <c>A.B.C</c>, <c>global::A.B</c> o <c>A</c>.</summary>
    private static string? UltimoNombre(SyntaxNode nodo) => nodo switch
    {
        MemberAccessExpressionSyntax acceso => acceso.Name.Identifier.ValueText,
        QualifiedNameSyntax cualificado => cualificado.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax conAlias => conAlias.Name.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => null,
    };

    /// <summary>
    /// Las definiciones de colección y las clases de test de un conjunto de fuentes. Hace falta el conjunto, no
    /// fichero a fichero: un atributo de test propio (<c>class TeoriaConMockupsAttribute : TheoryAttribute</c>) y
    /// una clase base con tests pueden vivir en otro fichero que la clase que los usa.
    ///
    /// <para>
    /// <b>Clase de test</b>: declaración de clase (o <c>record</c> de clase) no abstracta que declara al menos un
    /// método con un atributo de test, o que hereda de una clase del conjunto que los tiene. La comprobación es
    /// <b>por declaración</b>: dos clases en un fichero son dos entradas, y cada parte de una <c>partial</c> que
    /// declare tests necesita su propio atributo (quien lee el fichero no ve el de la otra parte).
    /// </para>
    /// </summary>
    public static ColeccionesDeSuite Colecciones(IEnumerable<(string Ruta, string Texto)> fuentes)
    {
        var tipos = fuentes
            .SelectMany(f => CSharpSyntaxTree.ParseText(f.Texto).GetRoot().DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .Where(EsClase)
                .Select(t => (f.Ruta, Tipo: t)))
            .ToList();

        var atributosDeTest = CerrarPorHerencia(
            AtributosDeTestDeXunit,
            tipos.Select(t => (Nombre: SinSufijoAttribute(t.Tipo.Identifier.ValueText), Bases: Bases(t.Tipo).Select(SinSufijoAttribute).ToList())));

        var testsPropios = tipos.ToDictionary(
            t => t.Tipo,
            t => t.Tipo.Members.OfType<MethodDeclarationSyntax>().Count(m => Atributos(m.AttributeLists).Any(a => atributosDeTest.Contains(NombreDeAtributo(a)))));

        var conTests = CerrarPorHerencia(
            tipos.Where(t => testsPropios[t.Tipo] > 0).Select(t => t.Tipo.Identifier.ValueText),
            tipos.Select(t => (Nombre: t.Tipo.Identifier.ValueText, Bases: Bases(t.Tipo).ToList())));

        var definiciones = new List<DefinicionDeColeccion>();
        var clases = new List<ClaseDeTest>();

        foreach (var (ruta, tipo) in tipos)
        {
            foreach (var atributo in Atributos(tipo.AttributeLists).Where(a => NombreDeAtributo(a) == "CollectionDefinition"))
            {
                definiciones.Add(new DefinicionDeColeccion(
                    ruta, NombreCompleto(tipo), LineaDe(atributo), LiteralLlano(PrimerArgumento(atributo)), atributo.ToString()));
            }

            var heredaDe = Bases(tipo).FirstOrDefault(conTests.Contains);
            if (tipo.Modifiers.Any(SyntaxKind.AbstractKeyword) || (testsPropios[tipo] == 0 && heredaDe is null))
                continue;

            var (via, coleccion) = ViaDe(tipo);
            clases.Add(new ClaseDeTest(ruta, NombreCompleto(tipo), LineaDe(tipo.Identifier), via, coleccion, testsPropios[tipo], heredaDe));
        }

        return new ColeccionesDeSuite(definiciones, clases, atributosDeTest.OrderBy(a => a, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// La forma canónica es la que lee un guion de reparto que no compila nada: una lista de atributos que es,
    /// carácter a carácter, <c>[Collection("Nombre")]</c>. Todo lo demás que xUnit aceptaría es <c>NoLiteral</c>.
    /// </summary>
    private static (ViaDeColeccion Via, string? Coleccion) ViaDe(TypeDeclarationSyntax tipo)
    {
        var listas = tipo.AttributeLists.Where(l => l.Attributes.Any(a => NombreDeAtributo(a) == "Collection")).ToList();
        if (listas.Count == 0)
            return (ViaDeColeccion.SinAtributo, null);

        var escrito = string.Join(" ", listas.Select(l => l.ToString()));
        if (listas.Count != 1 || listas[0].Attributes.Count != 1)
            return (ViaDeColeccion.NoLiteral, escrito);

        var nombre = LiteralLlano(PrimerArgumento(listas[0].Attributes[0]));
        return nombre is not null && escrito == $"[Collection(\"{nombre}\")]"
            ? (ViaDeColeccion.Literal, nombre)
            : (ViaDeColeccion.NoLiteral, escrito);
    }

    /// <summary>El valor de un literal de cadena corriente y sin escapes (<c>"X"</c>); <c>null</c> en cualquier otro caso.</summary>
    private static string? LiteralLlano(AttributeArgumentSyntax? argumento)
    {
        if (argumento is not { NameColon: null, NameEquals: null, Expression: LiteralExpressionSyntax literal }
            || !literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return null;
        }

        var valor = literal.Token.ValueText;
        return literal.Token.Text == $"\"{valor}\"" ? valor : null;
    }

    /// <summary>El nombre va primero; lo que siga (<c>DisableParallelization = true</c>) no lo cambia.</summary>
    private static AttributeArgumentSyntax? PrimerArgumento(AttributeSyntax atributo) =>
        atributo.ArgumentList?.Arguments.FirstOrDefault();

    /// <summary>
    /// Punto fijo: parte de <paramref name="semilla"/> y añade todo tipo que herede (por nombre simple) de uno ya
    /// incluido, hasta que no entra ninguno más.
    /// </summary>
    private static HashSet<string> CerrarPorHerencia(IEnumerable<string> semilla, IEnumerable<(string Nombre, List<string> Bases)> tipos)
    {
        var cerrado = semilla.ToHashSet(StringComparer.Ordinal);
        var candidatos = tipos.ToList();
        bool crecio;

        do
        {
            crecio = false;
            foreach (var (nombre, bases) in candidatos)
            {
                if (bases.Any(cerrado.Contains) && cerrado.Add(nombre))
                    crecio = true;
            }
        }
        while (crecio);

        return cerrado;
    }

    private static bool EsClase(TypeDeclarationSyntax tipo) =>
        tipo is ClassDeclarationSyntax || (tipo is RecordDeclarationSyntax registro && !registro.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword));

    private static IEnumerable<string> Bases(TypeDeclarationSyntax tipo) =>
        tipo.BaseList?.Types.Select(b => UltimoNombre(b.Type)).OfType<string>() ?? [];

    private static IEnumerable<AttributeSyntax> Atributos(SyntaxList<AttributeListSyntax> listas) =>
        listas.SelectMany(l => l.Attributes);

    private static string NombreDeAtributo(AttributeSyntax atributo) =>
        SinSufijoAttribute(UltimoNombre(atributo.Name) ?? atributo.Name.ToString());

    private static string SinSufijoAttribute(string nombre) =>
        nombre.Length > "Attribute".Length && nombre.EndsWith("Attribute", StringComparison.Ordinal)
            ? nombre[..^"Attribute".Length]
            : nombre;

    /// <summary><c>Exterior.Interior</c> para una clase anidada: el nombre simple no la distinguiría de otra.</summary>
    private static string NombreCompleto(TypeDeclarationSyntax tipo) =>
        string.Join(".", tipo.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText));

    private static int LineaDe(SyntaxNode nodo) => nodo.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static int LineaDe(SyntaxToken token) => token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    /// <summary>Los <c>.cs</c> de <c>tests/CaeManager.E2ETests</c>, sin <c>bin/</c> ni <c>obj/</c>, con la ruta relativa a la raíz y <c>/</c>.</summary>
    public static List<(string Ruta, string Texto)> LeerE2E()
    {
        var carpeta = Path.Combine(FuentesDeSrc.RaizDelRepositorio(), "tests", "CaeManager.E2ETests");
        var s = Path.DirectorySeparatorChar;

        return Directory.EnumerateFiles(carpeta, "*.cs", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{s}obj{s}") && !a.Contains($"{s}bin{s}"))
            .Select(a => (Ruta: FuentesDeSrc.Relativa(a), Texto: File.ReadAllText(a)))
            .OrderBy(f => f.Ruta, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Las esperas fijas de un conjunto de fuentes como medida de <see cref="ListaCongelada"/>: <c>fichero :: llamada = n</c>.</summary>
    public static Dictionary<Ubicacion, int> MedirEsperasFijas(IEnumerable<(string Ruta, string Texto)> fuentes) =>
        fuentes
            .SelectMany(f => EsperasFijas(f.Texto).Select(e => new Ubicacion(f.Ruta, e.Simbolo)))
            .GroupBy(u => u)
            .ToDictionary(g => g.Key, g => g.Count());
}
