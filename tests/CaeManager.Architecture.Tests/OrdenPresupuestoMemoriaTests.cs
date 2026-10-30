using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// El 503 por presupuesto de memoria agotado (<c>UsePresupuestoMemoriaAgotadoComo503</c>) ha de ir
/// detrás de <c>UseCabecerasSeguridad</c> en la tubería de <c>Program.cs</c>: así el 503 se traduce sobre la respuesta que esas
/// cabeceras ya dejaron y solo reaplica lo suyo (ver <c>PresupuestoMemoriaAgotadoExtensions</c>). El
/// comportamiento lo prueba
/// <c>PresupuestoMemoriaAgotadoComo503Tests</c> (Web.Tests); este trinquete solo vigila el orden del
/// cableado. Se lee el árbol sintáctico de <c>Program.cs</c>, no el texto: un cambio de formato, de
/// nombre de la variable del builder o un comentario que mencione las llamadas no lo rompe.
/// </summary>
public class OrdenPresupuestoMemoriaTests
{
    [Fact]
    public void El_middleware_de_503_va_detras_de_las_cabeceras_de_seguridad()
    {
        var programa = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));
        var raiz = CSharpSyntaxTree.ParseText(programa).GetRoot();

        var cabeceras = PosicionesDeLlamada(raiz, "UseCabecerasSeguridad");
        var presupuesto = PosicionesDeLlamada(raiz, "UsePresupuestoMemoriaAgotadoComo503");

        cabeceras.Should().ContainSingle("Program.cs debe registrar las cabeceras de seguridad una sola vez");
        presupuesto.Should().ContainSingle("Program.cs debe registrar el 503 por presupuesto agotado una sola vez");
        presupuesto[0].Should().BeGreaterThan(cabeceras[0],
            "el 503 ha de ir detrás de las cabeceras de seguridad para conservarlas");
    }

    private static List<int> PosicionesDeLlamada(SyntaxNode raiz, string metodo) =>
        raiz.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(llamada => llamada.Expression is MemberAccessExpressionSyntax acceso
                && acceso.Name.Identifier.ValueText == metodo)
            .Select(llamada => llamada.SpanStart)
            .ToList();

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
            " — este test necesita el árbol fuente del repositorio.");
    }
}
