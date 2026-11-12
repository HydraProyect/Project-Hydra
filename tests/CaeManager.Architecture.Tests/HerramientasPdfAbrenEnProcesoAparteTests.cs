using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Unir PDF y Dividir PDF no abren con PDFsharp, en el proceso web, un PDF que ha subido el usuario: PDFsharp carga todo el
/// PDF al abrirlo y no deja acotar ese trabajo, así que un PDF hostil que pase los filtros previos tumbaría la aplicación.
/// Los dos motores (<c>PdfSharpUnidorPdf</c> y <c>PdfSharpDivisorPdf</c>) solo corren en el proceso aparte, que tiene un
/// límite duro de memoria y de tiempo, y el proceso web solo ve sus envoltorios.
///
/// <para>
/// <b>Contrato efectivo.</b> El detector es sintáctico y mira todo <c>src</c>: cuenta los identificadores de código (no los
/// comentarios ni las referencias de la documentación) que nombran a un motor, y los de <c>PdfReader</c> dentro de los
/// directorios de Herramientas PDF. NO ve un motor alcanzado por reflexión ni un <c>PdfReader</c> fuera de esos
/// directorios (Firmas, Plantillas y la lectura con IA lo usan con sus propias guardas, fuera de esta herramienta). Las
/// implementaciones de <c>IUnidorPdf</c> e <c>IDivisorPdf</c> se buscan también por sintaxis, en la lista de bases.
/// </para>
/// </summary>
public class HerramientasPdfAbrenEnProcesoAparteTests
{
    private static readonly string[] Motores = ["PdfSharpUnidorPdf", "PdfSharpDivisorPdf"];

    /// <summary>Los propios motores y el punto de entrada del proceso aparte, que es el único que los construye.</summary>
    private static readonly string[] FicherosQuePuedenNombrarLosMotores =
        ["PdfSharpUnidorPdf.cs", "PdfSharpDivisorPdf.cs", "ProcesoHerramientasPdf.cs"];

    private static readonly string[] DirectoriosDeLaHerramienta =
    [
        "src/CaeManager.Application/HerramientasPdf",
        "src/CaeManager.Infrastructure/HerramientasPdf",
        "src/CaeManager.Web/Features/HerramientasPdf",
    ];

    private static string RaizDelRepositorio()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.sln"))
               && !Directory.GetFiles(directorio.FullName, "*.slnx").Any())
            directorio = directorio.Parent;

        return directorio?.FullName ?? throw new InvalidOperationException("No se encuentra la raíz del repositorio.");
    }

    private static bool EsGenerado(string ruta) =>
        ruta.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj");

    private static List<(string Fichero, SyntaxNode Raiz)> Fuentes(IEnumerable<string> directorios) =>
        directorios
            .SelectMany(d => Directory.GetFiles(Path.Combine(RaizDelRepositorio(), d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !EsGenerado(f))
            .Select(f => (Path.GetFileName(f), CSharpSyntaxTree.ParseText(File.ReadAllText(f)).GetRoot()))
            .ToList();

    /// <summary>Identificadores de código: <c>DescendantNodes</c> no entra en la documentación ni en los comentarios.</summary>
    private static IEnumerable<string> Identificadores(SyntaxNode raiz) =>
        raiz.DescendantNodes().OfType<SimpleNameSyntax>().Select(n => n.Identifier.ValueText);

    private static List<string> Implementaciones(List<(string Fichero, SyntaxNode Raiz)> fuentes, string puerto) =>
        fuentes
            .SelectMany(f => f.Raiz.DescendantNodes().OfType<TypeDeclarationSyntax>())
            .Where(t => t.BaseList?.Types.Any(b => b.Type is SimpleNameSyntax { Identifier.ValueText: var nombre } && nombre == puerto) == true)
            .Select(t => t.Identifier.ValueText)
            .ToList();

    [Fact]
    public void Solo_el_proceso_aparte_nombra_los_motores_de_PDFsharp_de_Unir_y_Dividir()
    {
        var fuentes = Fuentes(["src"]);
        fuentes.Should().HaveCountGreaterThan(500, "si baja de golpe, el escáner dejó de mirar src");

        var quienLosNombra = fuentes
            .Where(f => Identificadores(f.Raiz).Any(Motores.Contains))
            .Select(f => f.Fichero)
            .Distinct()
            .ToList();

        quienLosNombra.Should().Contain("ProcesoHerramientasPdf.cs", "si no, el detector no ve las referencias que debería");
        quienLosNombra.Should().BeSubsetOf(FicherosQuePuedenNombrarLosMotores,
            "el proceso web no construye ni registra un motor de PDFsharp: Unir y Dividir abren en el proceso aparte");
    }

    /// <summary>
    /// El punto de entrada del proceso aparte (<c>ProcesoHerramientasPdf.Ejecutar</c>) puede nombrar los motores, así que
    /// llamarlo desde el proceso web abriría el PDF en él. Solo lo llama Program.cs, dentro del <c>if</c> que reconoce el
    /// argumento del proceso aparte, antes de construir la aplicación. Sintáctico, como el resto: no ve una llamada por
    /// reflexión ni un alias del tipo.
    /// </summary>
    [Fact]
    public void El_punto_de_entrada_del_proceso_aparte_solo_se_llama_al_arrancar_como_proceso_aparte()
    {
        var fuentes = Fuentes(["src"]);

        var llamadas = fuentes
            .SelectMany(f => f.Raiz.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.Expression is MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "Ejecutar",
                    Expression: var tipo,
                } && tipo.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().Any(n => n.Identifier.ValueText == "ProcesoHerramientasPdf"))
                .Select(i => (f.Fichero, Llamada: i)))
            .ToList();

        llamadas.Should().NotBeEmpty("si no, el detector no ve la llamada que debería");
        llamadas.Select(l => l.Fichero).Should().OnlyContain(f => f == "Program.cs",
            "el proceso web no entra en el proceso aparte: solo Program.cs lo hace, al arrancar con su argumento");
        llamadas.Should().OnlyContain(l => l.Llamada.Ancestors().OfType<IfStatementSyntax>()
                .Any(si => Identificadores(si.Condition).Contains("Argumento")
                           && Identificadores(si.Condition).Contains("ProcesoHerramientasPdf")),
            "fuera del if que reconoce el argumento, la aplicación web abriría el PDF en su propio proceso");
    }

    [Fact]
    public void En_Herramientas_PDF_solo_los_motores_del_proceso_aparte_usan_PdfReader()
    {
        var fuentes = Fuentes(DirectoriosDeLaHerramienta);

        var conPdfReader = fuentes
            .Where(f => Identificadores(f.Raiz).Contains("PdfReader"))
            .Select(f => f.Fichero)
            .ToList();

        conPdfReader.Should().BeEquivalentTo(["PdfSharpUnidorPdf.cs", "PdfSharpDivisorPdf.cs"],
            "fuera de los motores del proceso aparte, Herramientas PDF no abre PDF con PDFsharp");
    }

    [Fact]
    public void Unir_y_Dividir_solo_tienen_la_implementacion_que_manda_el_PDF_al_proceso_aparte()
    {
        var fuentes = Fuentes(["src"]);

        Implementaciones(fuentes, "IUnidorPdf").Should().Equal("UnidorPdfEnProcesoAparte");
        Implementaciones(fuentes, "IDivisorPdf").Should().Equal("DivisorPdfEnProcesoAparte");
    }
}
