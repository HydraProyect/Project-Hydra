using CaeManager.E2ETests;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// La fixture de E2E solo da LOGIN a <c>cae_app_runtime</c> (rol de clúster, contraseña de pruebas pública) en un
/// clúster local declarado desechable (revisión de Codex en la PR #2032; ver <c>GuardaClusterDePruebas</c>, compilada
/// aquí por enlace desde <c>tests/CaeManager.E2ETests</c>). Dos propiedades, por separado:
///
/// <list type="number">
/// <item><b>La guarda falla cerrada.</b> Sin declaración, con una declaración que no es el identificador del clúster
/// conectado, o con un anfitrión que no es de bucle local, lanza. Solo pasa con declaración coincidente y anfitriones
/// locales.</item>
/// <item><b>Ningún <c>ALTER ROLE</c> de la suite E2E se ejecuta sin pasar antes por la guarda.</b> Cada literal que
/// contiene <c>ALTER ROLE</c> vive en un método que, antes de ese literal y como sentencia propia, llama a
/// <c>GuardaClusterDePruebas.ExigirClusterDesechable</c>. Sin esta propiedad, la primera podría quedar en verde con la
/// llamada borrada de la fixture.</item>
/// </list>
///
/// <para>
/// <b>Contrato efectivo.</b> El análisis de la segunda es sintáctico y solo de los <c>.cs</c> de
/// <c>tests/CaeManager.E2ETests</c>. NO ve un <c>ALTER ROLE</c> construido por concatenación de trozos que no lo
/// contengan, ni que la llamada a la guarda quede dentro de un <c>try</c> que se trague la excepción. Que la guarda se
/// ejerce de verdad lo demuestra CI: cada job de E2E arranca con un clúster virgen, así que la primera fixture siempre
/// pasa por el <c>ALTER ROLE</c> y, sin la declaración, el job falla.
/// </para>
/// </summary>
public class GuardaClusterDePruebasTests
{
    private const string Identificador = "7412345678901234567";
    private const string Local = "Host=localhost;Username=postgres;Password=postgres";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sin_declaracion_falla_sin_tocar_el_rol(string? declaracion)
    {
        var accion = () => GuardaClusterDePruebas.ExigirClusterDesechable(declaracion, Local, Identificador);

        accion.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{GuardaClusterDePruebas.VariableDeEntorno}*")
            .WithMessage("*no ha modificado ningún rol*");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("7412345678901234568")]
    [InlineData("*")]
    public void Una_declaracion_de_otro_cluster_falla(string declaracion)
    {
        var accion = () => GuardaClusterDePruebas.ExigirClusterDesechable(declaracion, Local, Identificador);

        accion.Should().Throw<InvalidOperationException>().WithMessage("*apunta a otro clúster*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Sin_identificador_del_cluster_falla(string? identificadorDelCluster)
    {
        var accion = () => GuardaClusterDePruebas.ExigirClusterDesechable(Identificador, Local, identificadorDelCluster);

        accion.Should().Throw<InvalidOperationException>().WithMessage("*apunta a otro clúster*");
    }

    [Theory]
    [InlineData("Host=staging.talveg.es;Username=postgres;Password=postgres")]
    [InlineData("Host=10.0.0.5;Username=postgres;Password=postgres")]
    [InlineData("Host=postgres;Username=postgres;Password=postgres")]
    [InlineData("Host=localhost,db.example.com;Username=postgres;Password=postgres")]
    [InlineData("Host=/var/run/postgresql;Username=postgres;Password=postgres")]
    [InlineData("Host=localhost.example.com;Username=postgres;Password=postgres")]
    [InlineData("Username=postgres;Password=postgres")]
    public void Un_anfitrion_que_no_es_local_falla_aunque_la_declaracion_coincida(string cadena)
    {
        var accion = () => GuardaClusterDePruebas.ExigirClusterDesechable(Identificador, cadena, Identificador);

        accion.Should().Throw<InvalidOperationException>().WithMessage("*no es local*");
    }

    [Theory]
    [InlineData("Host=localhost;Username=postgres;Password=postgres")]
    [InlineData("Host=LOCALHOST;Port=5433;Username=postgres;Password=postgres")]
    [InlineData("Host=localhost:5433;Username=postgres;Password=postgres")]
    [InlineData("Host=127.0.0.1;Username=postgres;Password=postgres")]
    [InlineData("Host=::1;Username=postgres;Password=postgres")]
    [InlineData("Host=[::1]:5432;Username=postgres;Password=postgres")]
    [InlineData("Host=localhost,127.0.0.1;Username=postgres;Password=postgres")]
    public void Con_declaracion_coincidente_y_anfitrion_local_pasa(string cadena)
    {
        var accion = () => GuardaClusterDePruebas.ExigirClusterDesechable(Identificador, cadena, Identificador);

        accion.Should().NotThrow();
    }

    [Fact]
    public void La_declaracion_admite_espacios_alrededor()
    {
        var accion = () => GuardaClusterDePruebas.ExigirClusterDesechable($" {Identificador}\n", Local, Identificador);

        accion.Should().NotThrow();
    }

    [Fact]
    public void Ningun_ALTER_ROLE_de_la_suite_E2E_se_ejecuta_sin_pasar_antes_por_la_guarda()
    {
        var fuentes = AnalisisDeSuiteE2E.LeerE2E()
            .Select(f => (f.Ruta, Raiz: CSharpSyntaxTree.ParseText(f.Texto).GetRoot()))
            .ToList();
        fuentes.Should().HaveCountGreaterThan(50, "la suite E2E tiene más de cincuenta ficheros .cs");

        var (literales, desvios) = AlterRoleSinGuardaPrevia(fuentes);

        // Control del instrumento: hoy hay exactamente uno, el de WebAppFixture.DarLoginAlRolDeRuntimeAsync.
        literales.Should().BeGreaterThan(0, "WebAppFixture da LOGIN a cae_app_runtime con un ALTER ROLE");

        desvios.Should().BeEmpty(
            "un ALTER ROLE cambia un objeto de clúster: antes de él, y en el mismo método, la fixture tiene que llamar a " +
            "GuardaClusterDePruebas.ExigirClusterDesechable para no abrir el rol en un clúster que no es de pruebas");
    }

    /// <summary>Control del instrumento de la segunda propiedad: detecta cada forma de saltarse la guarda.</summary>
    [Theory]
    [InlineData("async Task M() { await Ejecutar($\"ALTER ROLE {Rol} LOGIN\"); }", 1)]
    [InlineData("async Task M() { await Ejecutar(\"ALTER ROLE x LOGIN\"); GuardaClusterDePruebas.ExigirClusterDesechable(a, b, c); }", 1)]
    [InlineData("async Task M() { Action f = () => GuardaClusterDePruebas.ExigirClusterDesechable(a, b, c); await Ejecutar(\"ALTER ROLE x LOGIN\"); }", 1)]
    [InlineData("void Otro() { GuardaClusterDePruebas.ExigirClusterDesechable(a, b, c); } async Task M() { await Ejecutar(\"alter role x login\"); }", 1)]
    [InlineData("async Task M() { var ok = Comprobar() && GuardaClusterDePruebas.ExigirClusterDesechable(a, b, c); await Ejecutar(\"ALTER ROLE x\"); }", 1)]
    [InlineData("async Task M() { GuardaClusterDePruebas.ExigirClusterDesechable(a, b, c); await Ejecutar($\"ALTER ROLE {Rol} LOGIN\"); }", 0)]
    public void El_analisis_detecta_un_ALTER_ROLE_sin_guarda_previa(string metodos, int desviosEsperados)
    {
        var raiz = CSharpSyntaxTree.ParseText($"class Fixture {{ {metodos} }}").GetRoot();

        var (literales, desvios) = AlterRoleSinGuardaPrevia([("Fixture.cs", raiz)]);

        literales.Should().Be(1);
        desvios.Should().HaveCount(desviosEsperados);
    }

    private static (int Literales, List<string> Desvios) AlterRoleSinGuardaPrevia(IEnumerable<(string Ruta, SyntaxNode Raiz)> fuentes)
    {
        var literales = fuentes
            .SelectMany(f => f.Raiz.DescendantTokens()
                .Where(t => EsTextoDeCadena(t) && t.ValueText.Contains("ALTER ROLE", StringComparison.OrdinalIgnoreCase))
                .Select(t => (f.Ruta, Token: t)))
            .ToList();

        var desvios = new List<string>();
        foreach (var (ruta, token) in literales)
        {
            var metodo = token.Parent?.AncestorsAndSelf().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
            var guardaPrevia = metodo?.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i =>
                i.Expression is MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.ValueText: nameof(GuardaClusterDePruebas) },
                    Name.Identifier.ValueText: nameof(GuardaClusterDePruebas.ExigirClusterDesechable),
                }
                && i.Parent is ExpressionStatementSyntax
                && i.Ancestors().OfType<BaseMethodDeclarationSyntax>().First() == metodo
                && !i.Ancestors().TakeWhile(a => a != metodo).Any(a => a is LambdaExpressionSyntax or LocalFunctionStatementSyntax)
                && i.SpanStart < token.SpanStart) ?? false;

            if (!guardaPrevia)
                desvios.Add($"{ruta}:{token.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
        }

        return (literales.Count, desvios);
    }

    private static bool EsTextoDeCadena(SyntaxToken token) => token.Kind() is
        SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken or SyntaxKind.SingleLineRawStringLiteralToken
        or SyntaxKind.MultiLineRawStringLiteralToken or SyntaxKind.Utf8StringLiteralToken;
}
