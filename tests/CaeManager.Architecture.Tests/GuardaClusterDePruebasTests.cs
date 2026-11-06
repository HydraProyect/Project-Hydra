using CaeManager.E2ETests;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Las suites de pruebas solo tocan roles de clúster —y en particular solo dan LOGIN a <c>cae_app_runtime</c> con la
/// contraseña de pruebas pública— en un clúster local declarado desechable (revisión de Codex en la PR #2032; ver
/// <c>GuardaClusterDePruebas</c>, que vive en <c>tests/CaeManager.E2ETests</c> y se compila aquí y en
/// <c>tests/CaeManager.IntegrationTests</c> por enlace). Tres propiedades, por separado:
///
/// <list type="number">
/// <item><b>La guarda falla cerrada.</b> Sin declaración, con una declaración que no es el identificador del clúster
/// conectado, o con un anfitrión que no es de bucle local, lanza. Solo pasa con declaración coincidente y anfitriones
/// locales.</item>
/// <item><b>Ningún <c>ALTER ROLE</c> de las suites E2E y de integración se ejecuta sin pasar antes por la guarda.</b>
/// Cada literal que contiene <c>ALTER ROLE</c>, <c>ALTER USER</c> o <c>ALTER GROUP</c> (con cualquier espacio en medio)
/// es el primer argumento de <c>new NpgsqlCommand(sql, conexion, …)</c>, y antes de él, en el mismo método, fuera de
/// lambdas y en un bloque que lo contiene, hay una sentencia <c>await GuardaClusterDePruebas.ExigirClusterDesechableAsync(conexion, …)</c>
/// o <c>GuardaClusterDePruebas.ExigirClusterDesechableSincrono(conexion, …)</c> sobre esa misma conexión. La guarda
/// obtiene ella misma la declaración, los anfitriones y el identificador, así que quien la llama no puede pasarle
/// argumentos que la anulen; y el texto de los dos envoltorios fija qué le pasan a la decisión pura, para que tampoco
/// su cuerpo la vuelva tautológica. Sin esta propiedad, la primera podría quedar en verde con la llamada borrada o
/// movida.</item>
/// <item><b>El bootstrap de integración pasa la guarda antes de ejecutar nada.</b> El inicializador de módulo
/// <c>BootstrapDeClusterEnTests.Ejecutar</c> ejecuta además <c>deploy/bootstrap/roles-de-cluster.sql</c>, que converge
/// roles sin ningún literal <c>ALTER ROLE</c> en C#: la guarda es una sentencia de primer nivel de su cuerpo y precede a
/// todo <c>new NpgsqlCommand</c> del método.</item>
/// </list>
///
/// <para>
/// <b>Contrato efectivo.</b> El análisis de la segunda es sintáctico y solo de los <c>.cs</c> de
/// <c>tests/CaeManager.E2ETests</c> y <c>tests/CaeManager.IntegrationTests</c>. NO ve un cambio de rol construido por
/// concatenación de trozos que no contengan la orden entera, ni SQL leído de un fichero (por eso la tercera propiedad),
/// ni que la llamada a la guarda quede dentro de un <c>try</c> que se trague la excepción, ni que la variable de la
/// conexión se reasigne entre la guarda y el <c>ALTER</c>, ni un <c>goto</c> que salte la guarda. Que la guarda se
/// ejerce de verdad lo demuestra CI: cada job de E2E o de integración arranca con un clúster virgen, la primera fixture
/// de E2E siempre pasa por el <c>ALTER ROLE</c> y el inicializador de módulo de integración corre siempre, así que, sin
/// una declaración que coincida, el job falla.
/// </para>
///
/// <para>
/// <b>Excepción declarada.</b> <see cref="LiteralesExentosDeIntegracion"/> lista, por fichero y texto exacto, los
/// literales que el análisis caza pero que no cambian un rol de clúster: hoy dos, el nombre del caso y el SQL del
/// <c>ALTER ROLE … IN DATABASE … SET</c> con que <c>EquivalenciaDelClonConLaBaseMigradaTests</c> comprueba la
/// sensibilidad de su huella sobre una base
/// temporal que el propio test crea y elimina. No da LOGIN, no pone contraseña ni cambia atributos del rol, y su proceso
/// ya pasó la guarda en el inicializador de módulo. Una exención que deja de existir hace fallar el test.
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

        desvios.Select(d => d.Ubicacion).Should().BeEmpty(
            "un ALTER ROLE cambia un objeto de clúster: va en un new NpgsqlCommand(sql, conexion, …) y, antes de él, en el " +
            "mismo método y en un bloque que lo contenga, la fixture hace await GuardaClusterDePruebas." +
            "ExigirClusterDesechableAsync(conexion, …) con esa misma conexión, para no abrir el rol en un clúster que no es " +
            "de pruebas");
    }

    [Fact]
    public void Ningun_ALTER_ROLE_de_la_suite_de_integracion_se_ejecuta_sin_pasar_antes_por_la_guarda()
    {
        var fuentes = LeerIntegracion()
            .Select(f => (f.Ruta, Raiz: CSharpSyntaxTree.ParseText(f.Texto).GetRoot()))
            .ToList();
        fuentes.Should().HaveCountGreaterThan(50, "la suite de integración tiene más de cincuenta ficheros .cs");

        var (literales, desvios) = AlterRoleSinGuardaPrevia(fuentes);

        // Control del instrumento: hoy son seis (el LOGIN del bootstrap, tres en ContratoDeRolesDeClusterTests y los dos
        // exentos de EquivalenciaDelClonConLaBaseMigradaTests, la etiqueta del caso y su SQL).
        literales.Should().BeGreaterThan(LiteralesExentosDeIntegracion.Count,
            "BootstrapDeClusterEnTests da LOGIN a cae_app_runtime con un ALTER ROLE");

        var exentosVistos = desvios.Where(d => LiteralesExentosDeIntegracion.Contains((d.Ruta, d.Texto))).ToList();
        exentosVistos.Select(d => (d.Ruta, d.Texto)).Should().BeEquivalentTo(LiteralesExentosDeIntegracion,
            "una exención que ya no corresponde a ningún literal sobra y se borra de la lista");

        desvios.Except(exentosVistos).Select(d => d.Ubicacion).Should().BeEmpty(
            "un ALTER ROLE cambia un objeto de clúster: va en un new NpgsqlCommand(sql, conexion, …) y, antes de él, en el " +
            "mismo método y en un bloque que lo contenga, el test pasa por GuardaClusterDePruebas con esa misma conexión, " +
            "para no tocar el rol en un clúster que no es de pruebas");
    }

    /// <summary>
    /// El inicializador de módulo de integración ejecuta <c>roles-de-cluster.sql</c> (leído de un fichero, invisible para
    /// el análisis de literales) y el <c>ALTER ROLE … LOGIN PASSWORD</c>: la guarda síncrona, sobre su conexión, es una
    /// sentencia de primer nivel del método y va antes de cualquier <c>NpgsqlCommand</c>.
    /// </summary>
    [Fact]
    public void El_bootstrap_de_integracion_pasa_la_guarda_antes_de_ejecutar_ningun_comando()
    {
        var inicializadores = LeerIntegracion()
            .SelectMany(f => CSharpSyntaxTree.ParseText(f.Texto).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.AttributeLists.SelectMany(a => a.Attributes)
                    .Any(a => a.Name.ToString() is "ModuleInitializer" or "ModuleInitializerAttribute"
                        || a.Name.ToString().EndsWith(".ModuleInitializer", StringComparison.Ordinal)
                        || a.Name.ToString().EndsWith(".ModuleInitializerAttribute", StringComparison.Ordinal)))
                .Select(m => (f.Ruta, Metodo: m)))
            .ToList();

        inicializadores.Select(i => i.Ruta).Should().Equal(["tests/CaeManager.IntegrationTests/BootstrapDeClusterEnTests.cs"],
            "un inicializador de módulo corre antes que cualquier test: si hubiera otro, podría tocar roles sin pasar por " +
            "este análisis");

        DesviosDelBootstrap(inicializadores[0].Metodo).Should().BeEmpty();
    }

    [Theory]
    [InlineData("sin guarda",
        "[ModuleInitializer] static void E() { using var c = new NpgsqlConnection(x); c.Open(); using var g = new NpgsqlCommand(guion, c); g.ExecuteNonQuery(); }")]
    [InlineData("guarda después del guion",
        "[ModuleInitializer] static void E() { using var c = new NpgsqlConnection(x); c.Open(); using var g = new NpgsqlCommand(guion, c); g.ExecuteNonQuery(); GuardaClusterDePruebas.ExigirClusterDesechableSincrono(c, null); }")]
    [InlineData("guarda dentro de un if",
        "[ModuleInitializer] static void E() { using var c = new NpgsqlConnection(x); c.Open(); if (y) { GuardaClusterDePruebas.ExigirClusterDesechableSincrono(c, null); } using var g = new NpgsqlCommand(guion, c); }")]
    [InlineData("guarda sobre otra conexión",
        "[ModuleInitializer] static void E() { using var c = new NpgsqlConnection(x); c.Open(); GuardaClusterDePruebas.ExigirClusterDesechableSincrono(otra, null); using var g = new NpgsqlCommand(guion, c); }")]
    [InlineData("CreateCommand antes de la guarda",
        "[ModuleInitializer] static void E() { using var c = new NpgsqlConnection(x); c.Open(); using var k = c.CreateCommand(); k.CommandText = guion; k.ExecuteNonQuery(); GuardaClusterDePruebas.ExigirClusterDesechableSincrono(c, null); using var g = new NpgsqlCommand(guion, c); }")]
    [InlineData("new con tipo inferido antes de la guarda",
        "[ModuleInitializer] static void E() { using var c = new NpgsqlConnection(x); c.Open(); NpgsqlCommand k = new(guion, c); k.ExecuteNonQuery(); GuardaClusterDePruebas.ExigirClusterDesechableSincrono(c, null); using var g = new NpgsqlCommand(guion, c); }")]
    [InlineData("ayudante que ejecuta SQL antes de la guarda",
        "[ModuleInitializer] static void E() { using var c = new NpgsqlConnection(x); c.Open(); Ejecutar(c, guion); GuardaClusterDePruebas.ExigirClusterDesechableSincrono(c, null); using var g = new NpgsqlCommand(guion, c); }")]
    public void El_analisis_del_bootstrap_detecta_una_guarda_ausente_o_tardia(string caso, string metodo)
    {
        var declaracion = CSharpSyntaxTree.ParseText($"class B {{ {metodo} }}").GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single();

        DesviosDelBootstrap(declaracion).Should().NotBeEmpty(caso);
    }

    [Fact]
    public void El_analisis_del_bootstrap_acepta_la_guarda_antes_del_primer_comando()
    {
        var declaracion = CSharpSyntaxTree.ParseText(
                "class B { [ModuleInitializer] static void E() { using var c = new NpgsqlConnection(x); c.Open(); " +
                "GuardaClusterDePruebas.ExigirClusterDesechableSincrono(c, null); using var g = new NpgsqlCommand(guion, c); " +
                "g.ExecuteNonQuery(); using var l = new NpgsqlCommand(\"ALTER ROLE r LOGIN\", c); l.ExecuteNonQuery(); } }")
            .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        DesviosDelBootstrap(declaracion).Should().BeEmpty();
    }

    /// <summary>
    /// Los dos envoltorios (el asíncrono que llaman la fixture de E2E y los tests de integración, y el síncrono del
    /// inicializador de módulo) no degradan la decisión pura: le pasan la variable de entorno, la cadena de la conexión
    /// recibida y el identificador leído por esa misma conexión, sin capturar la excepción. Fijado por texto normalizado
    /// porque sin servidor no se puede ejecutar.
    /// </summary>
    [Theory]
    [InlineData(nameof(GuardaClusterDePruebas.ExigirClusterDesechableAsync), "awaitconsulta.ExecuteScalarAsync()")]
    [InlineData(nameof(GuardaClusterDePruebas.ExigirClusterDesechableSincrono), "consulta.ExecuteScalar()")]
    public void El_envoltorio_pasa_a_la_decision_los_datos_del_cluster_conectado(string envoltorio, string lectura)
    {
        var guarda = AnalisisDeSuiteE2E.LeerE2E().Single(f => f.Ruta.EndsWith("GuardaClusterDePruebas.cs", StringComparison.Ordinal));
        var metodo = CSharpSyntaxTree.ParseText(guarda.Texto).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == envoltorio);
        string Normal(SyntaxNode n) => string.Concat(n.ToString().Where(c => !char.IsWhiteSpace(c)));

        metodo.ParameterList.Parameters.Select(p => p.Identifier.ValueText).Should().Equal("conexion", "transaccion");
        metodo.DescendantNodes().OfType<TryStatementSyntax>().Should().BeEmpty("la guarda no se traga su propia excepción");

        var consulta = metodo.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Should().ContainSingle().Subject;
        Normal(consulta).Should().Be("newNpgsql.NpgsqlCommand(ConsultaIdentificador,conexion,transaccion)",
            "el identificador se lee por la misma conexión y transacción que el ALTER ROLE");

        var asignacion = metodo.DescendantNodes().OfType<AssignmentExpressionSyntax>().Should().ContainSingle().Subject;
        Normal(asignacion).Should().Be($"identificadorDelCluster={lectura}asstring");

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
    [InlineData("guarda síncrona después del ALTER",
        "void M() { var c = new NpgsqlCommand(\"ALTER ROLE x LOGIN\", con); GuardaClusterDePruebas.ExigirClusterDesechableSincrono(con, null); }", 1)]
    [InlineData("guarda síncrona sobre otra conexión",
        "void M() { GuardaClusterDePruebas.ExigirClusterDesechableSincrono(otra, null); var c = new NpgsqlCommand(\"ALTER ROLE x LOGIN\", con); }", 1)]
    [InlineData("guarda síncrona en una rama que no domina al ALTER",
        "void M() { if (x) GuardaClusterDePruebas.ExigirClusterDesechableSincrono(con, null); var c = new NpgsqlCommand(\"ALTER ROLE x LOGIN\", con); }", 1)]
    [InlineData("guarda asíncrona sin await, llamada como si fuera la síncrona",
        "void M() { GuardaClusterDePruebas.ExigirClusterDesechableAsync(con, null); var c = new NpgsqlCommand(\"ALTER ROLE x LOGIN\", con); }", 1)]
    [InlineData("guarda síncrona dentro de una función local",
        "void M() { void G() { GuardaClusterDePruebas.ExigirClusterDesechableSincrono(con, null); } G(); var c = new NpgsqlCommand(\"ALTER ROLE x LOGIN\", con); }", 1)]
    [InlineData("correcto",
        "async Task M() { await GuardaClusterDePruebas.ExigirClusterDesechableAsync(con, tx); await using (var c = new Npgsql.NpgsqlCommand($\"ALTER ROLE {Rol} LOGIN\", con, tx)) await c.ExecuteNonQueryAsync(); }", 0)]
    [InlineData("correcto síncrono",
        "static void M() { using var con = new NpgsqlConnection(x); con.Open(); GuardaClusterDePruebas.ExigirClusterDesechableSincrono(con, null); using var c = new NpgsqlCommand($\"ALTER ROLE r LOGIN PASSWORD '{P}';\", con); c.ExecuteNonQuery(); }", 0)]
    public void El_analisis_detecta_un_ALTER_ROLE_sin_guarda_previa(string caso, string metodos, int desviosEsperados)
    {
        var raiz = CSharpSyntaxTree.ParseText($"class Fixture {{ {metodos} }}").GetRoot();

        var (literales, desvios) = AlterRoleSinGuardaPrevia([("Fixture.cs", raiz)]);

        literales.Should().Be(1, caso);
        desvios.Should().HaveCount(desviosEsperados, caso);
    }

    private static readonly System.Text.RegularExpressions.Regex CambioDeRol = new(
        @"\bALTER\s+(ROLE|USER|GROUP)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Literales de la suite de integración que el análisis caza pero que no cambian un rol de clúster (ver «Excepción
    /// declarada» en la cabecera). Por fichero y texto exacto: un literal nuevo, aunque se parezca, no hereda la exención.
    /// Cada entrada tiene que corresponder exactamente a un literal (ni cero ni dos).
    /// </summary>
    private static readonly HashSet<(string Ruta, string Texto)> LiteralesExentosDeIntegracion =
    [
        // Nombre del caso en la tabla de alteraciones (clave del diccionario), no SQL.
        ("tests/CaeManager.IntegrationTests/EquivalenciaDelClonConLaBaseMigradaTests.cs", "ALTER ROLE IN DATABASE SET"),
        ("tests/CaeManager.IntegrationTests/EquivalenciaDelClonConLaBaseMigradaTests.cs",
            "ALTER ROLE cae_app_runtime IN DATABASE {BASE} SET statement_timeout = '7s';"),
    ];

    /// <summary>Los <c>.cs</c> de <c>tests/CaeManager.IntegrationTests</c>, sin <c>bin/</c> ni <c>obj/</c>, con la ruta relativa a la raíz y <c>/</c>.</summary>
    private static List<(string Ruta, string Texto)> LeerIntegracion()
    {
        var carpeta = Path.Combine(FuentesDeSrc.RaizDelRepositorio(), "tests", "CaeManager.IntegrationTests");
        var s = Path.DirectorySeparatorChar;

        return Directory.EnumerateFiles(carpeta, "*.cs", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{s}obj{s}") && !a.Contains($"{s}bin{s}"))
            .Select(a => (Ruta: FuentesDeSrc.Relativa(a), Texto: File.ReadAllText(a)))
            .OrderBy(f => f.Ruta, StringComparer.Ordinal)
            .ToList();
    }

    private sealed record Desvio(string Ruta, string Texto, string Ubicacion);

    private static (int Literales, List<Desvio> Desvios) AlterRoleSinGuardaPrevia(IEnumerable<(string Ruta, SyntaxNode Raiz)> fuentes)
    {
        var literales = fuentes
            .SelectMany(f => f.Raiz.DescendantTokens()
                .Where(t => EsTextoDeCadena(t) && CambioDeRol.IsMatch(t.ValueText))
                .Select(t => (f.Ruta, Token: t)))
            .ToList();

        var desvios = new List<Desvio>();
        foreach (var (ruta, token) in literales)
        {
            if (!GuardaPreviaSobreLaMismaConexion(token))
                desvios.Add(new Desvio(ruta, token.ValueText, $"{ruta}:{token.GetLocation().GetLineSpan().StartLinePosition.Line + 1}"));
        }

        return (literales.Count, desvios);
    }

    /// <summary>
    /// Desvíos del inicializador de módulo de integración: la guarda síncrona sobre su conexión tiene que ser una
    /// sentencia de primer nivel del cuerpo y preceder a todo <c>new NpgsqlCommand</c> del método.
    /// </summary>
    private static List<string> DesviosDelBootstrap(MethodDeclarationSyntax metodo)
    {
        var desvios = new List<string>();
        var comandos = metodo.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(c => c.Type.ToString().EndsWith("NpgsqlCommand", StringComparison.Ordinal)
                && c.ArgumentList?.Arguments.Count >= 2)
            .ToList();
        if (comandos.Count == 0) desvios.Add("el inicializador no crea ningún NpgsqlCommand: el análisis ya no lo reconoce");

        var guardas = metodo.Body?.Statements.OfType<ExpressionStatementSyntax>()
            .Where(s => s.Expression is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.ValueText: nameof(GuardaClusterDePruebas) },
                    Name.Identifier.ValueText: nameof(GuardaClusterDePruebas.ExigirClusterDesechableSincrono),
                },
            })
            .ToList() ?? [];
        if (guardas.Count == 0)
        {
            desvios.Add("no hay una sentencia de primer nivel GuardaClusterDePruebas.ExigirClusterDesechableSincrono(conexion, …)");
            return desvios;
        }

        // Antes de la primera guarda, la conexión que guarda solo puede declararse y abrirse: cualquier otro uso
        // (CreateCommand, un NpgsqlBatch, new(sql, conexion) con tipo inferido, un ayudante que ejecute SQL) podría
        // tocar roles antes de la comprobación sin ser un new NpgsqlCommand reconocible.
        var primera = guardas.Min(g => g.SpanStart);
        foreach (var guarda in guardas)
        {
            var conexionGuardada = ((InvocationExpressionSyntax)guarda.Expression).ArgumentList.Arguments
                is [{ Expression: IdentifierNameSyntax { Identifier.ValueText: var nombre } }, ..] ? nombre : null;
            foreach (var uso in metodo.DescendantNodes().OfType<IdentifierNameSyntax>()
                         .Where(i => i.Identifier.ValueText == conexionGuardada && i.SpanStart < primera))
            {
                var esApertura = uso.Parent is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Open", Parent: InvocationExpressionSyntax { Parent: ExpressionStatementSyntax } } acceso
                    && acceso.Expression == uso;
                if (!esApertura)
                    desvios.Add($"línea {uso.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: «{uso.Parent}» usa la conexión antes de la guarda");
            }
        }

        foreach (var comando in comandos)
        {
            var conexion = (comando.ArgumentList!.Arguments[1].Expression as IdentifierNameSyntax)?.Identifier.ValueText;
            var protegido = guardas.Any(g =>
                g.Span.End <= comando.SpanStart
                && ((InvocationExpressionSyntax)g.Expression).ArgumentList.Arguments is [{ Expression: IdentifierNameSyntax { Identifier.ValueText: var c } }, ..]
                && c == conexion);
            if (!protegido)
                desvios.Add($"línea {comando.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {comando} sin guarda previa sobre su conexión");
        }

        return desvios;
    }

    /// <summary>
    /// El literal es el primer argumento de <c>new NpgsqlCommand(sql, conexion, …)</c> y, en el mismo método y fuera de
    /// lambdas y funciones locales, una sentencia <c>await GuardaClusterDePruebas.ExigirClusterDesechableAsync(conexion, …)</c>
    /// o <c>GuardaClusterDePruebas.ExigirClusterDesechableSincrono(conexion, …)</c> con esa misma conexión lo precede dentro
    /// de un bloque que lo contiene (la domina, sin saltos).
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
            ConexionDeLaGuarda(sentencia.Expression) is { } conexionDeLaGuarda
            && conexionDeLaGuarda == conexion
            && sentencia.AncestorsAndSelf().FirstOrDefault(EsCuerpoEjecutable) == metodo
            && sentencia.Parent is BlockSyntax bloque
            && bloque.Span.Contains(literal.Span)
            && sentencia.Span.End <= literal.SpanStart);
    }

    /// <summary>
    /// La conexión sobre la que la expresión de una sentencia pasa por la guarda, o <c>null</c> si no es una llamada a
    /// la guarda: <c>await GuardaClusterDePruebas.ExigirClusterDesechableAsync(c, …)</c> o, sin <c>await</c>,
    /// <c>GuardaClusterDePruebas.ExigirClusterDesechableSincrono(c, …)</c>. La asíncrona sin <c>await</c> no cuenta.
    /// </summary>
    private static string? ConexionDeLaGuarda(ExpressionSyntax expresion)
    {
        var (invocacion, esperada) = expresion is AwaitExpressionSyntax { Expression: InvocationExpressionSyntax i }
            ? (i, nameof(GuardaClusterDePruebas.ExigirClusterDesechableAsync))
            : (expresion as InvocationExpressionSyntax, nameof(GuardaClusterDePruebas.ExigirClusterDesechableSincrono));

        return invocacion is
        {
            Expression: MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.ValueText: nameof(GuardaClusterDePruebas) },
                Name.Identifier.ValueText: var nombre,
            },
            ArgumentList.Arguments: [{ Expression: IdentifierNameSyntax { Identifier.ValueText: var conexion } }, ..],
        } && nombre == esperada
            ? conexion
            : null;
    }

    private static bool EsCuerpoEjecutable(SyntaxNode nodo) =>
        nodo is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LambdaExpressionSyntax or LocalFunctionStatementSyntax;

    private static bool EsTextoDeCadena(SyntaxToken token) => token.Kind() is
        SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken or SyntaxKind.SingleLineRawStringLiteralToken
        or SyntaxKind.MultiLineRawStringLiteralToken or SyntaxKind.Utf8StringLiteralToken;
}
