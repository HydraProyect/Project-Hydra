using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Issue #2001: el registro de lecturas en Sesión Privilegiada solo es duradero en autocommit. La
/// guarda contra una transacción abierta está en RegistroLecturaSesionPrivilegiada (transacción de
/// EF o ambiente); una transacción abierta por SQL crudo no la ve ni el servicio ni la función de
/// PostgreSQL (con el protocolo extendido, statement_timestamp() no coincide con
/// transaction_timestamp() ni en autocommit, así que la función no puede distinguirla). Este
/// trinquete cierra esa vía: ninguna cadena de <c>src</c> empieza una transacción con
/// <c>BEGIN</c> o <c>START TRANSACTION</c>. Las transacciones se abren con BeginTransaction.
///
/// Mira todas las cadenas (literales, interpoladas y crudas), no solo las que se pasan en la misma
/// línea a ExecuteSqlRaw, ExecuteSqlInterpolated, SqlQuery o CommandText: así también cuenta una
/// constante o un campo que se pasa después.
/// </summary>
public class SinTransaccionesAbiertasPorSqlCrudoTests
{
    /// <summary>
    /// Lista cerrada de cadenas que pueden abrir una transacción por SQL crudo, como
    /// «ruta relativa a src:línea». Vacía: medido al crearla, ninguna lo hace.
    /// </summary>
    private static readonly HashSet<string> Permitidas = [];

    [Fact]
    public void Ninguna_cadena_de_src_abre_una_transaccion_por_SQL_crudo()
    {
        var src = Path.Combine(BuscarRaizDelRepositorio(), "src");
        var ficheros = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        ficheros.Should().HaveCountGreaterThan(500, "el instrumento tiene que recorrer src entero");

        var cadenasVistas = 0;
        var infractoras = new List<string>();
        foreach (var fichero in ficheros)
        {
            var relativa = Path.GetRelativePath(src, fichero).Replace('\\', '/');
            foreach (var (linea, texto) in Cadenas(File.ReadAllText(fichero)))
            {
                cadenasVistas++;
                var ubicacion = $"{relativa}:{linea}";
                if (AbreTransaccion(texto) && !Permitidas.Contains(ubicacion))
                    infractoras.Add(ubicacion);
            }
        }

        cadenasVistas.Should().BeGreaterThan(1000, "el instrumento tiene que ver las cadenas de src");
        infractoras.Should().BeEmpty(
            "una transacción abierta por SQL crudo no la ve la guarda del registro de lecturas en Sesión " +
            "Privilegiada; abre la transacción con BeginTransaction");
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

    private static readonly Regex CuerpoEntreDolares = new(
        @"\$(?<etiqueta>[A-Za-z_]\w*|)\$.*?(\$\k<etiqueta>\$|\z)", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex InicioDeTransaccion = new(
        @"^(BEGIN(\s+(TRANSACTION|WORK|ISOLATION|READ|NOT|DEFERRABLE)\b.*)?|START\s+TRANSACTION\b.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// Si alguna sentencia del texto (fuera de comentarios, literales y cuerpos <c>$$ … $$</c>, donde
    /// <c>BEGIN</c> abre un bloque plpgsql y no una transacción) es <c>BEGIN</c> o
    /// <c>START TRANSACTION</c>.
    /// </summary>
    internal static bool AbreTransaccion(string sql)
    {
        var sinCuerpos = CuerpoEntreDolares.Replace(sql, " ");
        var (texto, enLiteral) = RequestsDelImportadorRegistranLecturaPrivilegiadaTests.Escanear(sinCuerpos);
        var codigo = new string(texto.Select((c, i) => enLiteral[i] ? ' ' : c).ToArray());
        return codigo.Split(';').Any(sentencia => InicioDeTransaccion.IsMatch(sentencia.Trim()));
    }

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
    public void El_detector_ve_una_transaccion_abierta_por_SQL(string sql) =>
        AbreTransaccion(sql).Should().BeTrue();

    [Theory]
    [InlineData("DO $$ BEGIN PERFORM 1; END $$;")]
    [InlineData("CREATE FUNCTION f() RETURNS int AS $f$ DECLARE x int; BEGIN RETURN 1; END $f$ LANGUAGE plpgsql;")]
    [InlineData("CREATE FUNCTION f() RETURNS int LANGUAGE plpgsql AS $$\nDECLARE\n  v int;\nBEGIN\n  RETURN 1;\nEND;\n$$;")]
    [InlineData("SELECT 'BEGIN;'")]
    [InlineData("SELECT E'\\'; BEGIN; '")]
    [InlineData("-- BEGIN;\nSELECT 1")]
    [InlineData("/* BEGIN; /* START TRANSACTION; */ */ SELECT 1")]
    [InlineData("-----BEGIN CERTIFICATE-----")]
    [InlineData("Begin import")]
    [InlineData("SELECT set_config('app.contexto', @contexto, false);")]
    [InlineData("COMMIT;")]
    public void El_detector_no_confunde_bloques_plpgsql_comentarios_ni_literales(string sql) =>
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

    private static string BuscarRaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;
        actual.Should().NotBeNull("los tests tienen que correr dentro del repositorio");
        return actual!.FullName;
    }
}
