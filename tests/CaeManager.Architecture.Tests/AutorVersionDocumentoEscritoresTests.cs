using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// D-12: toda versión de un Documento nace con quien la sube. La renovación lo exige por compilación
/// (<c>Documento.NuevoDelMismoTitular</c> recibe el autor sin valor por defecto); las cinco factorías lo reciben como
/// parámetro opcional para que los seeders sigan sin autor (D-12.1), así que este instrumento es el que obliga a los
/// productores de verdad: fuera de los seeders, cada llamada a una factoría pasa <c>autor:</c> con nombre, y el
/// conjunto de ficheros que crean Documentos está cerrado. Un productor nuevo rompe este test hasta que se añade al
/// inventario y pasa el autor.
/// </summary>
public class AutorVersionDocumentoEscritoresTests
{
    private static readonly HashSet<string> Factorias =
        ["DeTrabajador", "DeCliente", "DeEmpresa", "DeVehiculo", "DeProyecto"];

    /// <summary>Los productores con persona detrás, todos con <c>AutorDeLaSubida</c>.</summary>
    private static readonly string[] ProductoresConocidos =
    [
        "src/CaeManager.Application/Documentos/Commands/CrearDocumento/CrearDocumentoCommand.cs",
        "src/CaeManager.Application/Importacion/Commands/EjecutarImportacion/EjecutarImportacionCommand.cs",
        "src/CaeManager.Application/Plantillas/Commands/GenerarDocumentoIndividual/GenerarDocumentoIndividualCommandHandler.cs",
    ];

    [Fact]
    public void Fuera_de_los_seeders_cada_factoria_de_Documento_recibe_el_autor_por_nombre()
    {
        var llamadas = LlamadasAFactorias().ToList();

        llamadas.Should().NotBeEmpty("el instrumento tiene que ver las factorías de los productores");
        llamadas.Where(l => !l.ConAutor).Select(l => l.Ubicacion).Should().BeEmpty(
            "una versión creada por una persona sin autor caería en D-12.1 y la podría validar quien la subió");
    }

    [Fact]
    public void Los_ficheros_que_crean_Documentos_fuera_de_los_seeders_son_los_del_inventario()
    {
        LlamadasAFactorias().Select(l => l.Fichero).Distinct().Should().BeEquivalentTo(ProductoresConocidos,
            "un productor nuevo de versiones se inventaría y resuelve su autor con AutorDeLaSubida (D-12)");
    }

    [Theory]
    [InlineData("var d = Documento.DeTrabajador(t, tipo, hoy, v, url);", false)]
    [InlineData("var d = Documento.DeEmpresa(e, tipo, hoy, v, url, autor: autor);", true)]
    [InlineData("var d = CaeManager.Domain.Documentos.Documento.DeProyecto(p, tipo, hoy, v, null, null, null, autor);", false)]
    public void El_instrumento_distingue_una_factoria_con_y_sin_autor(string sentencia, bool conAutor)
    {
        var arbol = CSharpSyntaxTree.ParseText($"class C {{ void M() {{ {sentencia} }} }}");

        var llamadas = Llamadas(arbol, "prueba.cs").ToList();

        llamadas.Should().ContainSingle().Which.ConAutor.Should().Be(conAutor);
    }

    private sealed record Llamada(string Fichero, string Ubicacion, bool ConAutor);

    private static IEnumerable<Llamada> LlamadasAFactorias()
    {
        var raiz = RaizDelRepositorio();
        var separador = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine(raiz, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separador}bin{separador}", StringComparison.Ordinal)
                && !f.Contains($"{separador}obj{separador}", StringComparison.Ordinal)
                // Sin persona detrás: los seeders dejan la versión sin autor (D-12.1).
                && !f.Contains($"{separador}Persistence{separador}Seed{separador}", StringComparison.Ordinal)
                && !f.Contains($"{separador}CaeManager.Migrations.PostgreSQL{separador}", StringComparison.Ordinal))
            .SelectMany(f => Llamadas(
                CSharpSyntaxTree.ParseText(File.ReadAllText(f)),
                Path.GetRelativePath(raiz, f).Replace(separador, '/')));
    }

    private static IEnumerable<Llamada> Llamadas(SyntaxTree arbol, string fichero) =>
        arbol.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: var nombre,
                Expression: var receptor
            } && Factorias.Contains(nombre) && NombreFinal(receptor) == "Documento")
            .Select(i => new Llamada(
                fichero,
                $"{fichero}:{i.GetLocation().GetLineSpan().StartLinePosition.Line + 1}",
                i.ArgumentList.Arguments.Any(a => a.NameColon?.Name.Identifier.ValueText == "autor")));

    private static string? NombreFinal(ExpressionSyntax expresion) => expresion switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax acceso => acceso.Name.Identifier.ValueText,
        _ => null
    };

    private static string RaizDelRepositorio()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.GetFiles("*.slnx").Any() && !dir.GetFiles("*.sln").Any())
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }
}
