using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Con qué identidad de PostgreSQL sirven el tráfico las aplicaciones que arranca la suite E2E (mejora 1.7 del
/// harness, 2026-11-05). <c>WebAppFixture</c> lo hace como <c>cae_app_runtime</c>, sometido a RLS y a los GRANT, igual
/// que producción; la única salida es sobrescribir <c>MotivoTraficoComoPropietario</c> con el motivo escrito. Tres
/// propiedades, por separado:
///
/// <list type="number">
/// <item><b>La clase base sirve como runtime por defecto.</b> <c>MotivoTraficoComoPropietario</c> es <c>virtual</c> y
/// devuelve <c>null</c>, y la clase escribe <c>ConnectionStrings__CaeManagerDbRuntime</c>. Sin esto, cambiar la base
/// devolvería las dieciséis fixtures al propietario con las otras dos propiedades en verde.</item>
/// <item><b>Las fixtures que sobrescriben <c>MotivoTraficoComoPropietario</c> son exactamente las congeladas.</b> Una
/// fixture nueva que sirve el tráfico como el rol propietario apaga RLS para todas las clases de su colección; no entra
/// sin que alguien la escriba aquí, con su motivo, que es el punto en que se pregunta si un contrato o una identidad de la
/// aplicación resolvía el caso.</item>
/// <item><b>Las cadenas de conexión de la aplicación solo se escriben en la clase <c>WebAppFixture</c></b>, no en sus
/// subclases (tampoco en las que viven en el mismo fichero), y nadie en la suite llama a
/// <c>SetEnvironmentVariable</c>. Una subclase que fijara <c>ConnectionStrings__CaeManagerDbRuntime</c> vacía en
/// <c>VariablesDeEntornoAdicionales</c> —que se aplica después y la pisa— devolvería el tráfico al propietario sin pasar por
/// la propiedad anterior. Se busca <c>CaeManagerDb</c> sin distinguir mayúsculas, que cubre las dos cadenas y las dos
/// formas de clave (<c>:</c> y <c>__</c>).</item>
/// </list>
///
/// <para>
/// <b>Contrato efectivo.</b> El análisis es sintáctico y solo de los <c>.cs</c> de <c>tests/CaeManager.E2ETests</c>. La
/// tercera propiedad mira el texto de los literales de cadena (también los interpolados y los <c>"""</c>), no
/// comentarios. NO ve una clave construida por concatenación de trozos que no contengan <c>CaeManagerDb</c>, ni una
/// variable de entorno fijada fuera de la suite (el job de CI no fija ninguna de las dos). Cada arranque de fixture
/// comprueba además en ejecución que hay conexiones como <c>cae_app_runtime</c> (<c>WebAppFixture.InitializeAsync</c>).
/// </para>
/// </summary>
public class IdentidadDeTraficoDeE2ETests
{
    private const string Propiedad = "MotivoTraficoComoPropietario";
    private const string ClaseBase = "WebAppFixture";
    private const string MarcaDeCadena = "CaeManagerDb";
    private const string ClaveDeRuntime = "ConnectionStrings__CaeManagerDbRuntime";

    /// <summary>
    /// Fixtures de E2E cuyo tráfico conecta como el rol propietario, exento de RLS. Hoy ninguna. Añadir una exige el motivo
    /// escrito al lado y justificarlo en la PR; un test que cruza Tenants no es motivo.
    /// </summary>
    private static readonly string[] FixturesConTraficoComoPropietario = [];

    private static List<(string Ruta, SyntaxNode Raiz)> Fuentes()
    {
        var fuentes = AnalisisDeSuiteE2E.LeerE2E()
            .Select(f => (f.Ruta, CSharpSyntaxTree.ParseText(f.Texto).GetRoot()))
            .ToList();

        // Control del instrumento: una lectura vacía daría verde por ausencia.
        fuentes.Should().HaveCountGreaterThan(50, "la suite E2E tiene más de cincuenta ficheros .cs");
        return fuentes;
    }

    [Fact]
    public void La_clase_base_sirve_el_trafico_como_runtime_por_defecto()
    {
        var bases = Fuentes()
            .SelectMany(f => f.Raiz.DescendantNodes().OfType<ClassDeclarationSyntax>())
            .Where(c => c.Identifier.ValueText == ClaseBase)
            .ToList();
        bases.Should().ContainSingle($"hay una sola clase {ClaseBase}");
        var clase = bases[0];

        var propiedad = clase.Members.OfType<PropertyDeclarationSyntax>().SingleOrDefault(p => p.Identifier.ValueText == Propiedad);
        propiedad.Should().NotBeNull($"{ClaseBase} declara {Propiedad}");
        propiedad!.Modifiers.Any(SyntaxKind.VirtualKeyword).Should().BeTrue($"{Propiedad} es virtual");
        propiedad.ExpressionBody?.Expression.IsKind(SyntaxKind.NullLiteralExpression).Should().BeTrue(
            $"{ClaseBase}.{Propiedad} devuelve null: por defecto el tráfico corre como cae_app_runtime");

        clase.DescendantTokens().Where(EsTextoDeCadena).Select(t => t.ValueText).Should().Contain(ClaveDeRuntime,
            $"{ClaseBase} fija {ClaveDeRuntime} al arrancar la aplicación");
    }

    [Fact]
    public void Las_fixtures_que_sirven_el_trafico_como_propietario_son_exactamente_las_congeladas()
    {
        var medidas = Fuentes()
            .SelectMany(f => f.Raiz.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            .Where(p => p.Identifier.ValueText == Propiedad && p.Modifiers.Any(SyntaxKind.OverrideKeyword))
            .Select(p => p.Ancestors().OfType<TypeDeclarationSyntax>().First().Identifier.ValueText)
            .ToList();

        medidas.Should().BeEquivalentTo(FixturesConTraficoComoPropietario,
            "el tráfico de los E2E corre como cae_app_runtime, igual que producción. NUEVA: si la fixture de verdad necesita " +
            "servir el tráfico como el rol propietario (exento de RLS), añádela a FixturesConTraficoComoPropietario con el " +
            "motivo y justifícalo en la PR; un test que cruza Tenants necesita un contrato o una identidad de la aplicación " +
            "que lo permita, no quitarle RLS al servidor. RETIRADA: bórrala de la lista");
    }

    [Fact]
    public void Las_cadenas_de_conexion_de_la_aplicacion_solo_se_fijan_en_la_clase_base()
    {
        var desvios = new List<string>();
        foreach (var (ruta, raiz) in Fuentes())
        {
            foreach (var token in raiz.DescendantTokens())
            {
                var esCadena = EsTextoDeCadena(token) && token.Text.Contains(MarcaDeCadena, StringComparison.OrdinalIgnoreCase);
                var esEntorno = token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText == "SetEnvironmentVariable";
                if (!esCadena && !esEntorno) continue;

                var clase = token.Parent?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                if (esCadena && clase?.Identifier.ValueText == ClaseBase) continue;

                desvios.Add($"{ruta}:{token.GetLocation().GetLineSpan().StartLinePosition.Line + 1} ({token.ValueText})");
            }
        }

        desvios.Should().BeEmpty(
            $"las cadenas de conexión de la aplicación ({MarcaDeCadena}*) solo las fija la clase {ClaseBase}, y nadie en la " +
            "suite fija variables de entorno del proceso: fuera de ahí una subclase podría devolver el tráfico al rol " +
            "propietario sin pasar por MotivoTraficoComoPropietario");
    }

    private static bool EsTextoDeCadena(SyntaxToken token) => token.Kind() is
        SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken or SyntaxKind.SingleLineRawStringLiteralToken
        or SyntaxKind.MultiLineRawStringLiteralToken or SyntaxKind.Utf8StringLiteralToken;
}
