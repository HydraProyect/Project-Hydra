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
/// contiene <c>ALTER ROLE</c>, <c>ALTER USER</c> o <c>ALTER GROUP</c> (con cualquier espacio en medio) es el primer
/// argumento de <c>new NpgsqlCommand(sql, conexion, …)</c>, y antes de él, en el mismo método, fuera de lambdas y en un
/// bloque que lo contiene, hay una sentencia <c>await GuardaClusterDePruebas.ExigirClusterDesechableAsync(conexion, …)</c>
/// sobre esa misma conexión. La guarda obtiene ella misma la declaración, los anfitriones y el identificador, así que
/// quien la llama no puede pasarle argumentos que la anulen; y el texto del envoltorio asíncrono fija qué le pasa a la
/// decisión pura, para que tampoco su cuerpo la vuelva tautológica. Sin esta propiedad, la primera podría quedar en verde con
/// la llamada borrada o movida en la fixture.</item>
/// </list>
///
/// <para>
/// <b>Contrato efectivo.</b> El análisis de la segunda es sintáctico y solo de los <c>.cs</c> de
/// <c>tests/CaeManager.E2ETests</c>. NO ve un cambio de rol construido por concatenación de trozos que no contengan la
/// orden entera, ni que la llamada a la guarda quede dentro de un <c>try</c> que se trague la excepción, ni que la
/// variable de la conexión se reasigne entre la guarda y el <c>ALTER</c>, ni un <c>goto</c> que salte la guarda. Que la
/// guarda se ejerce de verdad lo demuestra CI: cada job de E2E arranca con un clúster virgen, así que la primera fixture
/// siempre pasa por el <c>ALTER ROLE</c> y, sin una declaración que coincida, el job falla.
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
    [InlineData("Host=[::1]xyz;Username=postgres;Password=postgres")]
    [InlineData("Host=[127.0.0.1]basura;Username=postgres;Password=postgres")]
    [InlineData("Host=localhost:abc;Username=postgres;Password=postgres")]
    [InlineData("Host=localhost:;Username=postgres;Password=postgres")]
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
            "un ALTER ROLE cambia un objeto de clúster: va en un new NpgsqlCommand(sql, conexion, …) y, antes de él, en el " +
            "mismo método y en un bloque que lo contenga, la fixture hace await GuardaClusterDePruebas." +
            "ExigirClusterDesechableAsync(conexion, …) con esa misma conexión, para no abrir el rol en un clúster que no es " +
            "de pruebas");
    }

    /// <summary>
    /// El envoltorio que llama la fixture no degrada la decisión pura: le pasa la variable de entorno, la cadena de la
    /// conexión recibida y el identificador leído por esa misma conexión, sin capturar la excepción. Fijado por texto
    /// normalizado porque sin servidor no se puede ejecutar.
    /// </summary>
    [Fact]
    public void El_envoltorio_asincrono_pasa_a_la_decision_los_datos_del_cluster_conectado()
    {
        var guarda = AnalisisDeSuiteE2E.LeerE2E().Single(f => f.Ruta.EndsWith("GuardaClusterDePruebas.cs", StringComparison.Ordinal));
        var metodo = CSharpSyntaxTree.ParseText(guarda.Texto).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == nameof(GuardaClusterDePruebas.ExigirClusterDesechableAsync));
        string Normal(SyntaxNode n) => string.Concat(n.ToString().Where(c => !char.IsWhiteSpace(c)));

        metodo.ParameterList.Parameters.Select(p => p.Identifier.ValueText).Should().Equal("conexion", "transaccion");
        metodo.DescendantNodes().OfType<TryStatementSyntax>().Should().BeEmpty("la guarda no se traga su propia excepción");

        var consulta = metodo.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Should().ContainSingle().Subject;
        Normal(consulta).Should().Be("newNpgsql.NpgsqlCommand(ConsultaIdentificador,conexion,transaccion)",
            "el identificador se lee por la misma conexión y transacción que el ALTER ROLE");

        var asignacion = metodo.DescendantNodes().OfType<AssignmentExpressionSyntax>().Should().ContainSingle().Subject;
        Normal(asignacion).Should().Be("identificadorDelCluster=awaitconsulta.ExecuteScalarAsync()asstring");

        var decision = metodo.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is IdentifierNameSyntax { Identifier.ValueText: nameof(GuardaClusterDePruebas.ExigirClusterDesechable) })
            .Should().ContainSingle().Subject;
        Normal(decision.ArgumentList).Should().Be(
            "(Environment.GetEnvironmentVariable(VariableDeEntorno),conexion.ConnectionString,identificadorDelCluster)",
            "la declaración sale de la variable de entorno, los anfitriones de la conexión recibida y el identificador del " +
            "clúster leído por ella: cualquier otra combinación puede volver tautológica la comprobación");
        decision.Parent.Should().BeOfType<ExpressionStatementSyntax>();
    }

    /// <summary>Control del instrumento de la segunda propiedad: detecta cada forma de saltarse la guarda.</summary>
    [Theory]
    [InlineData("sin guarda",
        "async Task M() { await using var c = new Npgsql.NpgsqlCommand($\"ALTER ROLE {Rol} LOGIN\", con, tx); }", 1)]
    [InlineData("guarda después del ALTER",
        "async Task M() { var c = new NpgsqlCommand(\"ALTER ROLE x LOGIN\", con, tx); await GuardaClusterDePruebas.ExigirClusterDesechableAsync(con, tx); }", 1)]
    [InlineData("guarda dentro de una lambda",
        "async Task M() { Func<Task> f = async () => await GuardaClusterDePruebas.ExigirClusterDesechableAsync(con, tx); var c = new NpgsqlCommand(\"ALTER ROLE x\", con, tx); }", 1)]
    [InlineData("guarda en otro método",
        "async Task Otro() { await GuardaClusterDePruebas.ExigirClusterDesechableAsync(con, tx); } void M() { var c = new NpgsqlCommand(\"alter role x login\", con, tx); }", 1)]
    [InlineData("guarda en una rama que no domina al ALTER",
        "async Task M() { if (x) { await GuardaClusterDePruebas.ExigirClusterDesechableAsync(con, tx); } var c = new NpgsqlCommand(\"ALTER ROLE x\", con, tx); }", 1)]
    [InlineData("guarda sobre otra conexión",
        "async Task M() { await GuardaClusterDePruebas.ExigirClusterDesechableAsync(otra, tx); var c = new NpgsqlCommand(\"ALTER ROLE x\", con, tx); }", 1)]
    [InlineData("guarda sin await",
        "async Task M() { _ = GuardaClusterDePruebas.ExigirClusterDesechableAsync(con, tx); var c = new NpgsqlCommand(\"ALTER ROLE x\", con, tx); }", 1)]
    [InlineData("ALTER fuera de un NpgsqlCommand",
        "async Task M() { await GuardaClusterDePruebas.ExigirClusterDesechableAsync(con, tx); await Ejecutar(\"ALTER ROLE x\"); }", 1)]
    [InlineData("ALTER USER con espacios y salto de línea",
        "void M() { var c = new NpgsqlCommand(\"alter  \\n user x login\", con, tx); }", 1)]
    [InlineData("correcto",
        "async Task M() { await GuardaClusterDePruebas.ExigirClusterDesechableAsync(con, tx); await using (var c = new Npgsql.NpgsqlCommand($\"ALTER ROLE {Rol} LOGIN\", con, tx)) await c.ExecuteNonQueryAsync(); }", 0)]
    public void El_analisis_detecta_un_ALTER_ROLE_sin_guarda_previa(string caso, string metodos, int desviosEsperados)
    {
        var raiz = CSharpSyntaxTree.ParseText($"class Fixture {{ {metodos} }}").GetRoot();

        var (literales, desvios) = AlterRoleSinGuardaPrevia([("Fixture.cs", raiz)]);

        literales.Should().Be(1, caso);
        desvios.Should().HaveCount(desviosEsperados, caso);
    }

    private static readonly System.Text.RegularExpressions.Regex CambioDeRol = new(
        @"\bALTER\s+(ROLE|USER|GROUP)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static (int Literales, List<string> Desvios) AlterRoleSinGuardaPrevia(IEnumerable<(string Ruta, SyntaxNode Raiz)> fuentes)
    {
        var literales = fuentes
            .SelectMany(f => f.Raiz.DescendantTokens()
                .Where(t => EsTextoDeCadena(t) && CambioDeRol.IsMatch(t.ValueText))
                .Select(t => (f.Ruta, Token: t)))
            .ToList();

        var desvios = new List<string>();
        foreach (var (ruta, token) in literales)
        {
            if (!GuardaPreviaSobreLaMismaConexion(token))
                desvios.Add($"{ruta}:{token.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
        }

        return (literales.Count, desvios);
    }

    /// <summary>
    /// El literal es el primer argumento de <c>new NpgsqlCommand(sql, conexion, …)</c> y, en el mismo método y fuera de
    /// lambdas y funciones locales, una sentencia <c>await GuardaClusterDePruebas.ExigirClusterDesechableAsync(conexion, …)</c>
    /// con esa misma conexión lo precede dentro de un bloque que lo contiene (la domina, sin saltos).
    /// </summary>
    private static bool GuardaPreviaSobreLaMismaConexion(SyntaxToken literal)
    {
        var argumento = literal.Parent?.AncestorsAndSelf().OfType<ArgumentSyntax>().FirstOrDefault();
        if (argumento?.Parent is not ArgumentListSyntax { Parent: ObjectCreationExpressionSyntax creacion } lista
            || !creacion.Type.ToString().EndsWith("NpgsqlCommand", StringComparison.Ordinal)
            || lista.Arguments.IndexOf(argumento) != 0
            || lista.Arguments.Count < 2
            || lista.Arguments[1].Expression is not IdentifierNameSyntax { Identifier.ValueText: var conexion })
            return false;

        var metodo = creacion.AncestorsAndSelf().FirstOrDefault(EsCuerpoEjecutable);
        if (metodo is null or LambdaExpressionSyntax or LocalFunctionStatementSyntax) return false;

        return metodo.DescendantNodes().OfType<ExpressionStatementSyntax>().Any(sentencia =>
            sentencia.Expression is AwaitExpressionSyntax
            {
                Expression: InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax
                    {
                        Expression: IdentifierNameSyntax { Identifier.ValueText: nameof(GuardaClusterDePruebas) },
                        Name.Identifier.ValueText: nameof(GuardaClusterDePruebas.ExigirClusterDesechableAsync),
                    },
                    ArgumentList.Arguments: [{ Expression: IdentifierNameSyntax { Identifier.ValueText: var conexionDeLaGuarda } }, ..],
                },
            }
            && conexionDeLaGuarda == conexion
            && sentencia.AncestorsAndSelf().FirstOrDefault(EsCuerpoEjecutable) == metodo
            && sentencia.Parent is BlockSyntax bloque
            && bloque.Span.Contains(literal.Span)
            && sentencia.Span.End <= literal.SpanStart);
    }

    private static bool EsCuerpoEjecutable(SyntaxNode nodo) =>
        nodo is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LambdaExpressionSyntax or LocalFunctionStatementSyntax;

    private static bool EsTextoDeCadena(SyntaxToken token) => token.Kind() is
        SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken or SyntaxKind.SingleLineRawStringLiteralToken
        or SyntaxKind.MultiLineRawStringLiteralToken or SyntaxKind.Utf8StringLiteralToken;
}
