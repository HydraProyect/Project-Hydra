using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Con qué identidad de PostgreSQL sirven el tráfico las aplicaciones que arranca la suite E2E (mejora 1.7 del
/// harness, 2026-11-05). <c>WebAppFixture</c> lo hace como <c>cae_app_runtime</c>, sometido a RLS y a los GRANT, igual
/// que producción; la única salida es sobrescribir <c>MotivoTraficoComoPropietario</c> con el motivo escrito. Dos
/// propiedades, por separado:
///
/// <list type="number">
/// <item><b>Las fixtures que sobrescriben <c>MotivoTraficoComoPropietario</c> son exactamente las congeladas.</b> Una
/// fixture nueva que sirve el tráfico como el rol propietario apaga RLS para todas las clases de su colección; no entra
/// sin que alguien la escriba aquí, con su motivo, que es el punto en que se pregunta si un contrato o una identidad de la
/// aplicación resolvía el caso.</item>
/// <item><b>Las variables de conexión de la aplicación solo se escriben en <c>WebAppFixture.cs</c>.</b> Una subclase que
/// fijara <c>ConnectionStrings__CaeManagerDbRuntime</c> vacía en <c>VariablesDeEntornoAdicionales</c> devolvería el tráfico
/// al propietario sin pasar por la propiedad anterior. Se busca el prefijo <c>ConnectionStrings__CaeManagerDb</c>, que
/// cubre las dos cadenas.</item>
/// </list>
///
/// <para>
/// <b>Contrato efectivo.</b> El análisis es sintáctico y solo de los <c>.cs</c> de <c>tests/CaeManager.E2ETests</c>. La
/// segunda propiedad mira el texto de los literales de cadena (también los interpolados y los <c>"""</c>), no comentarios.
/// NO ve una clave construida por concatenación de trozos que no contengan el prefijo, ni una variable de entorno
/// fijada fuera de la fixture (el job de CI no fija ninguna de las dos).
/// </para>
/// </summary>
public class IdentidadDeTraficoDeE2ETests
{
    private const string Propiedad = "MotivoTraficoComoPropietario";
    private const string PrefijoDeCadena = "ConnectionStrings__CaeManagerDb";
    private const string FicheroDeLaFixture = "WebAppFixture.cs";

    /// <summary>
    /// Fixtures de E2E cuyo tráfico conecta como el rol propietario, exento de RLS. Hoy ninguna. Añadir una exige el motivo
    /// escrito al lado y justificarlo en la PR; un test que cruza Tenants no es motivo.
    /// </summary>
    private static readonly string[] FixturesConTraficoComoPropietario = [];

    [Fact]
    public void Las_fixtures_que_sirven_el_trafico_como_propietario_son_exactamente_las_congeladas()
    {
        var medidas = new List<string>();
        foreach (var (ruta, texto) in AnalisisDeSuiteE2E.LeerE2E())
        {
            var raiz = CSharpSyntaxTree.ParseText(texto).GetRoot();
            foreach (var propiedad in raiz.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                         .Where(p => p.Identifier.ValueText == Propiedad && p.Modifiers.Any(SyntaxKind.OverrideKeyword)))
            {
                var clase = propiedad.Ancestors().OfType<TypeDeclarationSyntax>().First().Identifier.ValueText;
                medidas.Add(clase);
            }
        }

        medidas.Should().BeEquivalentTo(FixturesConTraficoComoPropietario,
            "el tráfico de los E2E corre como cae_app_runtime, igual que producción. NUEVA: si la fixture de verdad necesita " +
            "servir el tráfico como el rol propietario (exento de RLS), añádela a FixturesConTraficoComoPropietario con el " +
            "motivo y justifícalo en la PR; un test que cruza Tenants necesita un contrato o una identidad de la aplicación " +
            "que lo permita, no quitarle RLS al servidor. RETIRADA: bórrala de la lista");
    }

    [Fact]
    public void Las_cadenas_de_conexion_de_la_aplicacion_solo_se_fijan_en_WebAppFixture()
    {
        var desvios = new List<string>();
        foreach (var (ruta, texto) in AnalisisDeSuiteE2E.LeerE2E())
        {
            if (Path.GetFileName(ruta) == FicheroDeLaFixture) continue;

            var raiz = CSharpSyntaxTree.ParseText(texto).GetRoot();
            foreach (var token in raiz.DescendantTokens().Where(EsTextoDeCadena))
            {
                if (token.Text.Contains(PrefijoDeCadena, StringComparison.Ordinal))
                    desvios.Add($"{ruta}:{token.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
            }
        }

        desvios.Should().BeEmpty(
            $"las variables {PrefijoDeCadena}* de la aplicación solo las fija {FicheroDeLaFixture}: fuera de ahí una subclase " +
            "podría devolver el tráfico al rol propietario sin pasar por MotivoTraficoComoPropietario");
    }

    private static bool EsTextoDeCadena(SyntaxToken token) => token.Kind() is
        SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken or SyntaxKind.SingleLineRawStringLiteralToken
        or SyntaxKind.MultiLineRawStringLiteralToken or SyntaxKind.Utf8StringLiteralToken;
}
