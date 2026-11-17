using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// D-12: toda versión de un Documento nace con quien la sube. La renovación lo exige por compilación
/// (<c>Documento.NuevoDelMismoTitular</c> recibe el autor sin valor por defecto); las cinco factorías lo reciben como
/// parámetro opcional para que los seeders sigan sin autor (D-12.1), así que este instrumento es el que obliga a los
/// productores de verdad. Fuera de los seeders: cada llamada a una factoría pasa <c>autor:</c> con nombre y con una
/// variable que sale de <c>AutorDeLaSubida</c> en el mismo fichero (nunca <c>default</c> ni
/// <c>AutorVersionDocumento.Desconocido</c>); nadie llega a las factorías por <c>using static</c>, alias o grupo de
/// métodos; ningún <c>.razor</c> las llama; y el conjunto de ficheros que crean Documentos está cerrado. Un productor
/// nuevo rompe este test hasta que se añade al inventario y pasa el autor.
/// </summary>
public class AutorVersionDocumentoEscritoresTests
{
    private static readonly HashSet<string> Factorias =
        ["DeTrabajador", "DeCliente", "DeEmpresa", "DeVehiculo", "DeProyecto"];

    private static readonly Regex FactoriaEnRazor =
        new(@"\bDocumento\s*\.\s*De(Trabajador|Cliente|Empresa|Vehiculo|Proyecto)\b");

    /// <summary>Los productores con persona detrás, todos con <c>AutorDeLaSubida</c>.</summary>
    private static readonly string[] ProductoresConocidos =
    [
        "src/CaeManager.Application/Documentos/Commands/CrearDocumento/CrearDocumentoCommand.cs",
        "src/CaeManager.Application/Importacion/Commands/EjecutarImportacion/EjecutarImportacionCommand.cs",
        "src/CaeManager.Application/Plantillas/Commands/GenerarDocumentoIndividual/GenerarDocumentoIndividualCommandHandler.cs",
    ];

    [Fact]
    public void Fuera_de_los_seeders_cada_factoria_de_Documento_recibe_el_autor_resuelto_por_nombre()
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

    [Fact]
    public void Fuera_de_los_seeders_nadie_llega_a_las_factorias_por_un_atajo_que_el_instrumento_no_ve()
    {
        FicherosDeProduccion("*.cs")
            .SelectMany(f => Atajos(CSharpSyntaxTree.ParseText(File.ReadAllText(f.Ruta)), f.Relativa))
            .Should().BeEmpty("un using static, un alias o un grupo de métodos esquivaría la comprobación del autor");
    }

    [Fact]
    public void Ningun_componente_razor_crea_Documentos_con_las_factorias()
    {
        var razor = FicherosDeProduccion("*.razor").ToList();

        razor.Should().NotBeEmpty("el instrumento tiene que ver los componentes");
        razor.Where(f => FactoriaEnRazor.IsMatch(File.ReadAllText(f.Ruta))).Select(f => f.Relativa)
            .Should().BeEmpty("un componente crea versiones por Crear o Renovar, que ya firman la autoría (D-12)");
    }

    [Theory]
    [InlineData("var d = Documento.DeTrabajador(t, tipo, hoy, v, url);", false)]
    [InlineData("var d = Documento.DeEmpresa(e, tipo, hoy, v, url, autor: autor);", true)]
    [InlineData("var d = Documento.DeEmpresa(e, tipo, hoy, v, url, autor: default);", false)]
    [InlineData("var d = Documento.DeEmpresa(e, tipo, hoy, v, url, autor: AutorVersionDocumento.Desconocido);", false)]
    [InlineData("var d = Documento.DeVehiculo(e, tipo, hoy, v, url, autor: new AutorVersionDocumento());", false)]
    [InlineData("var d = CaeManager.Domain.Documentos.Documento.DeProyecto(p, tipo, hoy, v, null, null, null, autor);", false)]
    public void El_instrumento_distingue_una_factoria_con_y_sin_autor(string sentencia, bool conAutor)
    {
        var arbol = CSharpSyntaxTree.ParseText(
            $"class C {{ async void M() {{ var autor = await AutorDeLaSubida.ResolverAsync(a); {sentencia} }} }}");

        var llamadas = Llamadas(arbol, "prueba.cs").ToList();

        llamadas.Should().ContainSingle().Which.ConAutor.Should().Be(conAutor);
    }

    [Fact]
    public void El_instrumento_no_acepta_una_variable_que_no_sale_de_AutorDeLaSubida()
    {
        var arbol = CSharpSyntaxTree.ParseText(
            "class C { void M() { var autor = AutorVersionDocumento.Desconocido; var d = Documento.DeEmpresa(e, t, h, v, autor: autor); } }");

        Llamadas(arbol, "prueba.cs").Should().ContainSingle().Which.ConAutor.Should().BeFalse();
    }

    [Theory]
    [InlineData("using static CaeManager.Domain.Documentos.Documento; class C { }")]
    [InlineData("using Doc = CaeManager.Domain.Documentos.Documento; class C { }")]
    [InlineData("class C { void M() { System.Func<System.Guid, object> f = Documento.DeTrabajador; } }")]
    public void El_instrumento_ve_los_atajos(string codigo)
    {
        Atajos(CSharpSyntaxTree.ParseText(codigo), "prueba.cs").Should().ContainSingle();
    }

    [Theory]
    [InlineData("var d = Documento.DeTrabajador(t, tipo, hoy, v);", true)]
    [InlineData("var d = Documento . DeCliente(", true)]
    [InlineData("await Mediator.Send(new CrearDocumentoCommand(", false)]
    public void El_instrumento_ve_una_factoria_en_un_razor(string codigo, bool encontrada)
    {
        FactoriaEnRazor.IsMatch(codigo).Should().Be(encontrada);
    }

    private sealed record Llamada(string Fichero, string Ubicacion, bool ConAutor);

    private sealed record Fichero(string Ruta, string Relativa);

    private static IEnumerable<Fichero> FicherosDeProduccion(string patron)
    {
        var raiz = RaizDelRepositorio();
        var separador = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine(raiz, "src"), patron, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separador}bin{separador}", StringComparison.Ordinal)
                && !f.Contains($"{separador}obj{separador}", StringComparison.Ordinal)
                // Sin persona detrás: los seeders dejan la versión sin autor (D-12.1).
                && !f.Contains($"{separador}Persistence{separador}Seed{separador}", StringComparison.Ordinal)
                && !f.Contains($"{separador}CaeManager.Migrations.PostgreSQL{separador}", StringComparison.Ordinal))
            .Select(f => new Fichero(f, Path.GetRelativePath(raiz, f).Replace(separador, '/')));
    }

    private static IEnumerable<Llamada> LlamadasAFactorias() =>
        FicherosDeProduccion("*.cs")
            .SelectMany(f => Llamadas(CSharpSyntaxTree.ParseText(File.ReadAllText(f.Ruta)), f.Relativa));

    private static IEnumerable<Llamada> Llamadas(SyntaxTree arbol, string fichero)
    {
        var raiz = arbol.GetRoot();
        // Las variables locales del fichero que salen de AutorDeLaSubida: las únicas que valen como autor.
        var resueltas = raiz.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Initializer?.Value is { } valor
                && valor.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>()
                    .Any(m => NombreFinal(m.Expression) == "AutorDeLaSubida"))
            .Select(v => v.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);

        return raiz.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: var nombre,
                Expression: var receptor
            } && Factorias.Contains(nombre) && NombreFinal(receptor) == "Documento")
            .Select(i => new Llamada(
                fichero,
                $"{fichero}:{i.GetLocation().GetLineSpan().StartLinePosition.Line + 1}",
                i.ArgumentList.Arguments.Any(a => a.NameColon?.Name.Identifier.ValueText == "autor"
                    && a.Expression is IdentifierNameSyntax variable
                    && resueltas.Contains(variable.Identifier.ValueText))));
    }

    private static IEnumerable<string> Atajos(SyntaxTree arbol, string fichero)
    {
        var raiz = arbol.GetRoot();
        foreach (var directiva in raiz.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if ((directiva.StaticKeyword != default || directiva.Alias is not null)
                && directiva.NamespaceOrType is { } destino && NombreFinal(destino) == "Documento")
                yield return Ubicacion(fichero, directiva);
        }

        foreach (var acceso in raiz.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (Factorias.Contains(acceso.Name.Identifier.ValueText) && NombreFinal(acceso.Expression) == "Documento"
                && !(acceso.Parent is InvocationExpressionSyntax invocacion && invocacion.Expression == acceso))
                yield return Ubicacion(fichero, acceso);
        }
    }

    private static string Ubicacion(string fichero, SyntaxNode nodo) =>
        $"{fichero}:{nodo.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";

    private static string? NombreFinal(SyntaxNode expresion) => expresion switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax acceso => acceso.Name.Identifier.ValueText,
        QualifiedNameSyntax calificado => calificado.Right.Identifier.ValueText,
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
