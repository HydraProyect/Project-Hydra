using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Issue #2001: el registro de lecturas en Sesión Privilegiada solo es duradero en autocommit. La
/// guarda contra una transacción abierta está en RegistroLecturaSesionPrivilegiada, que solo ve la
/// transacción de EF (<c>Database.CurrentTransaction</c>) y la ambiente
/// (<c>Transaction.Current</c>). Una transacción abierta por SQL crudo, o con BeginTransaction
/// directamente sobre la conexión sin pasar por EF, no la ve ni el servicio ni la función de
/// PostgreSQL (con el protocolo extendido, statement_timestamp() no coincide con
/// transaction_timestamp() ni en autocommit, así que la función no puede distinguirla).
///
/// Este trinquete cierra esas dos vías en <c>src</c>:
/// <list type="bullet">
/// <item>ninguna cadena (literal, interpolada o cruda) contiene en código SQL una sentencia
/// <c>BEGIN</c> o <c>START TRANSACTION</c>; mira todas las cadenas, no solo las que se pasan en la
/// misma línea a ExecuteSqlRaw, ExecuteSqlInterpolated, SqlQuery o CommandText, para que también
/// cuente una constante que se pasa después;</item>
/// <item>BeginTransaction y BeginTransactionAsync solo se llaman sobre <c>Database</c> de EF (que fija
/// <c>CurrentTransaction</c>), y EnlistTransaction no se llama, salvo la lista cerrada.</item>
/// </list>
/// </summary>
public class SinTransaccionesFueraDeEfTests
{
    /// <summary>
    /// Cadenas que pueden abrir una transacción por SQL crudo, como «ruta relativa a src:línea».
    /// Vacía: medido al crearla, ninguna lo hace.
    /// </summary>
    private static readonly HashSet<string> CadenasPermitidas = [];

    /// <summary>
    /// Ficheros que pueden abrir una transacción sobre la conexión sin EF, con su motivo. Medido al
    /// crearla: solo este uso en src.
    /// </summary>
    private static readonly Dictionary<string, string> ConexionesPermitidas = new()
    {
        ["CaeManager.Infrastructure/Persistence/Migraciones/TransicionLineaBaseCompactada.cs"] =
            "Transición de la línea base al arrancar: su propia NpgsqlConnection con la cadena del " +
            "propietario de las migraciones, sin DbContext ni Sesión Privilegiada, para reescribir " +
            "__EFMigrationsHistory bajo un candado consultivo.",
    };

    private static readonly HashSet<string> MetodosDeTransaccion = ["BeginTransaction", "BeginTransactionAsync", "EnlistTransaction"];

    [Fact]
    public void Ninguna_cadena_de_src_abre_una_transaccion_por_SQL_crudo()
    {
        var cadenasVistas = 0;
        var infractoras = new List<string>();
        foreach (var (relativa, fuente) in FicherosDeSrc())
        {
            foreach (var (linea, texto) in Cadenas(fuente))
            {
                cadenasVistas++;
                var ubicacion = $"{relativa}:{linea}";
                if (AbreTransaccion(texto) && !CadenasPermitidas.Contains(ubicacion))
                    infractoras.Add(ubicacion);
            }
        }

        cadenasVistas.Should().BeGreaterThan(1000, "el instrumento tiene que ver las cadenas de src");
        infractoras.Should().BeEmpty(
            "una transacción abierta por SQL crudo no la ve la guarda del registro de lecturas en Sesión " +
            "Privilegiada; abre la transacción con Database.BeginTransaction de EF");
    }

    [Fact]
    public void Nadie_en_src_abre_una_transaccion_sobre_la_conexion_sin_pasar_por_EF()
    {
        var llamadasVistas = 0;
        var infractoras = new List<string>();
        var permitidasUsadas = new HashSet<string>();
        foreach (var (relativa, fuente) in FicherosDeSrc())
        {
            foreach (var (linea, deEf) in LlamadasDeTransaccion(fuente))
            {
                llamadasVistas++;
                if (deEf)
                    continue;
                if (ConexionesPermitidas.ContainsKey(relativa))
                    permitidasUsadas.Add(relativa);
                else
                    infractoras.Add($"{relativa}:{linea}");
            }
        }

        llamadasVistas.Should().BeGreaterThan(5, "el instrumento tiene que ver las transacciones de EF de src");
        infractoras.Should().BeEmpty(
            "una transacción abierta sobre la conexión no fija Database.CurrentTransaction y la guarda del " +
            "registro de lecturas en Sesión Privilegiada no la ve; ábrela con Database.BeginTransaction de EF");
        permitidasUsadas.Should().BeEquivalentTo(ConexionesPermitidas.Keys,
            "una excepción de la lista cerrada que ya no se usa se quita de la lista");
    }

    private static IEnumerable<(string Relativa, string Fuente)> FicherosDeSrc()
    {
        var src = Path.Combine(BuscarRaizDelRepositorio(), "src");
        var separador = Path.DirectorySeparatorChar;
        var ficheros = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separador}obj{separador}", StringComparison.Ordinal)
                        && !f.Contains($"{separador}bin{separador}", StringComparison.Ordinal))
            .ToList();
        ficheros.Should().HaveCountGreaterThan(500, "el instrumento tiene que recorrer src entero");
        return ficheros.Select(f => (Path.GetRelativePath(src, f).Replace('\\', '/'), File.ReadAllText(f)));
    }

    /// <summary>Las cadenas de un fichero C#, con su línea; los huecos de una interpolada cuentan como un valor.</summary>
    internal static IEnumerable<(int Linea, string Texto)> Cadenas(string fuente)
    {
        var raiz = CSharpSyntaxTree.ParseText(fuente).GetRoot();
        foreach (var nodo in raiz.DescendantNodes())
        {
            string? texto = nodo switch
            {
                LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression)
                                               || l.IsKind(SyntaxKind.Utf8StringLiteralExpression) => l.Token.ValueText,
                InterpolatedStringExpressionSyntax i => string.Concat(i.Contents.Select(c => c switch
                {
                    InterpolatedStringTextSyntax t => t.TextToken.ValueText,
                    _ => " valor ",
                })),
                _ => null,
            };
            if (texto is not null)
                yield return (nodo.GetLocation().GetLineSpan().StartLinePosition.Line + 1, texto);
        }
    }

    /// <summary>
    /// Las llamadas a BeginTransaction, BeginTransactionAsync y EnlistTransaction de un fichero C#, y si
    /// van sobre <c>Database</c> de EF (<c>x.Database.BeginTransaction…</c>). Sin modelo semántico, el
    /// receptor se reconoce por nombre: cualquier otro receptor (una conexión, un acceso condicional,
    /// una variable) cuenta como transacción fuera de EF. EnlistTransaction nunca es de EF.
    /// </summary>
    internal static IEnumerable<(int Linea, bool DeEf)> LlamadasDeTransaccion(string fuente)
    {
        var raiz = CSharpSyntaxTree.ParseText(fuente).GetRoot();
        foreach (var llamada in raiz.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            (string? Nombre, ExpressionSyntax? Receptor) destino = llamada.Expression switch
            {
                MemberAccessExpressionSyntax m => (m.Name.Identifier.ValueText, m.Expression),
                MemberBindingExpressionSyntax b => (b.Name.Identifier.ValueText, null),
                IdentifierNameSyntax i => (i.Identifier.ValueText, null),
                GenericNameSyntax g => (g.Identifier.ValueText, null),
                _ => (null, null),
            };
            var (nombre, receptor) = destino;
            if (nombre is null || !MetodosDeTransaccion.Contains(nombre))
                continue;

            var sobreDatabase = receptor switch
            {
                MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText == "Database",
                IdentifierNameSyntax i => i.Identifier.ValueText == "Database",
                _ => false,
            };
            yield return (llamada.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                nombre != "EnlistTransaction" && sobreDatabase);
        }
    }

    private static readonly Regex InicioDeTransaccion = new(
        @"^(BEGIN(\s+(TRANSACTION|WORK|ISOLATION|READ|NOT|DEFERRABLE)\b.*)?|START\s+TRANSACTION\b.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// Si alguna sentencia del texto es <c>BEGIN</c> o <c>START TRANSACTION</c> en código, según el
    /// escáner léxico compartido: fuera de comentarios, literales, identificadores entre comillas y
    /// cadenas con dólar (los cuerpos <c>AS $$ … $$</c> de plpgsql, donde <c>BEGIN</c> abre un bloque
    /// y no una transacción, son cadenas con dólar).
    /// </summary>
    internal static bool AbreTransaccion(string sql) =>
        EscanerSql.SoloCodigo(sql).Split(';').Any(sentencia => InicioDeTransaccion.IsMatch(sentencia.Trim()));

    [Theory]
    [InlineData("BEGIN;")]
    [InlineData("begin")]
    [InlineData("  Begin ; SELECT app_registrar_actividad_soporte_por_sesion(1, 2, 3, 4)")]
    [InlineData("BEGIN TRANSACTION ISOLATION LEVEL READ COMMITTED;")]
    [InlineData("BEGIN WORK")]
    [InlineData("begin isolation level serializable")]
    [InlineData("START TRANSACTION;")]
    [InlineData("start transaction read only")]
    [InlineData("SET x.y = '1'; BEGIN; SELECT 1;")]
    [InlineData("SELECT 1; -- comentario\nBEGIN;")]
    [InlineData("DO $$ BEGIN PERFORM 1; END $$; BEGIN;")]
    [InlineData("SELECT '$$'; BEGIN;")]
    [InlineData("SELECT \"a$$b\"; BEGIN;")]
    [InlineData("SELECT 1 /* $$ */; BEGIN;")]
    public void El_detector_ve_una_transaccion_abierta_por_SQL(string sql) =>
        AbreTransaccion(sql).Should().BeTrue();

    [Theory]
    [InlineData("DO $$ BEGIN PERFORM 1; END $$;")]
    [InlineData("CREATE FUNCTION f() RETURNS int AS $f$ DECLARE x int; BEGIN RETURN 1; END $f$ LANGUAGE plpgsql;")]
    [InlineData("CREATE FUNCTION f() RETURNS int LANGUAGE plpgsql AS $$\nDECLARE\n  v int;\nBEGIN\n  RETURN 1;\nEND;\n$$;")]
    [InlineData("SELECT $x$ BEGIN; $x$;")]
    [InlineData("SELECT 'BEGIN;'")]
    [InlineData("SELECT E'\\'; BEGIN; '")]
    [InlineData("SELECT \"BEGIN\"")]
    [InlineData("SELECT 1; \"BEGIN\";")]
    [InlineData("-- BEGIN;\nSELECT 1")]
    [InlineData("/* BEGIN; /* START TRANSACTION; */ */ SELECT 1")]
    [InlineData("-----BEGIN CERTIFICATE-----")]
    [InlineData("Begin import")]
    [InlineData("SELECT set_config('app.contexto', @contexto, false);")]
    [InlineData("COMMIT;")]
    public void El_detector_no_confunde_bloques_plpgsql_comentarios_literales_ni_identificadores(string sql) =>
        AbreTransaccion(sql).Should().BeFalse();

    [Fact]
    public void El_detector_ve_las_cadenas_de_cualquier_forma_en_el_codigo()
    {
        const string fuente = """"
            class C
            {
                const string Inicio = "begin";
                void F(DbContext db, DbCommand cmd, int x)
                {
                    db.Database.ExecuteSqlRaw("BEGIN;");
                    db.Database.ExecuteSqlInterpolated($"START TRANSACTION; SELECT {x}");
                    cmd.CommandText = $$"""
                        BEGIN ISOLATION LEVEL SERIALIZABLE;
                        """;
                    db.Database.SqlQuery<string>($"SELECT f({x})");
                }
            }
            """";

        var cadenas = Cadenas(fuente).ToList();
        cadenas.Where(c => AbreTransaccion(c.Texto)).Select(c => c.Linea).Should().Equal(3, 6, 7, 8);
        cadenas.Should().Contain(c => c.Linea == 11 && !AbreTransaccion(c.Texto), "una consulta sin BEGIN no cuenta");
    }

    [Fact]
    public void El_detector_distingue_la_transaccion_de_EF_de_la_abierta_sobre_la_conexion()
    {
        const string fuente = """
            class C
            {
                async Task F(DbContext dbContext, NpgsqlConnection npgsql, IDbConnection idb, Transaction t)
                {
                    await using var a = await dbContext.Database.BeginTransactionAsync();
                    using var b = dbContext.Database.BeginTransaction(IsolationLevel.ReadCommitted);
                    await using var c = await dbContext.Database.GetDbConnection().BeginTransactionAsync();
                    using var d = dbContext.Database.GetDbConnection().BeginTransaction();
                    await using var e = await npgsql.BeginTransactionAsync();
                    using var f = idb.BeginTransaction();
                    using var g = npgsql?.BeginTransaction();
                    npgsql.EnlistTransaction(t);
                    dbContext.Database.GetDbConnection().EnlistTransaction(t);
                }
            }
            """;

        var llamadas = LlamadasDeTransaccion(fuente).ToList();
        llamadas.Where(l => l.DeEf).Select(l => l.Linea).Should().Equal(5, 6);
        llamadas.Where(l => !l.DeEf).Select(l => l.Linea).Should().Equal(7, 8, 9, 10, 11, 12, 13);
    }

    private static string BuscarRaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;
        actual.Should().NotBeNull("los tests tienen que correr dentro del repositorio");
        return actual!.FullName;
    }
}
