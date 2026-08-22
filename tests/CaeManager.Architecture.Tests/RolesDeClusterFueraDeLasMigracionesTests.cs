using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Las migraciones por base no crean ni destruyen roles de clúster.</b>
///
/// <para>
/// No es una preferencia de estilo: es la reparación de un fallo reproducido
/// tres veces en CI. <c>pg_authid</c> es un catálogo compartido, y crear un rol
/// desde la migración de UNA base ponía a seis migradores a competir por él.
/// La traza instrumentada dejó el suceso reconstruido: seis entran en la misma
/// migración en 9 ms y 125 ms después tres fallan con <c>42704</c> dentro de su
/// propio bloque, en la sentencia posterior a la creación protegida.
/// </para>
///
/// <para>
/// La especificación de los principales vive en
/// <c>deploy/bootstrap/roles-de-cluster.sql</c> y la ejecutan cinco adaptadores
/// —CI, desarrollo, VPS, ensayo de restauración y el arnés de tests— antes de
/// que arranque ningún migrador.
/// </para>
///
/// <para>
/// <b>Sin allowlist.</b> Si el diseño es correcto no queda ni una creación de
/// rol legítima bajo <c>Migrations/</c>, así que la lista de excepciones está
/// vacía y debe seguir estándolo. Una excepción aquí significaría que alguien
/// devolvió el objeto de clúster al ámbito equivocado.
/// </para>
/// </summary>
public class RolesDeClusterFueraDeLasMigracionesTests
{
    /// <summary>
    /// Las formas de crear o destruir un principal. <c>CREATE USER</c> es
    /// sinónimo exacto de <c>CREATE ROLE … LOGIN</c> en PostgreSQL, así que
    /// vigilar solo la primera dejaría la puerta abierta. El SQL dinámico entra
    /// por el mismo patrón: lo que se busca es el texto, esté donde esté.
    /// </summary>
    private static readonly Regex Prohibido = new(
        @"\b(CREATE|DROP)\s+(ROLE|USER|GROUP)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Ejecutar SQL desde C# en el ensamblado de migraciones, esquivando
    /// <c>migrationBuilder.Sql</c>. No hay ningún uso legítimo hoy, y sería la
    /// vía por la que una creación programática se escaparía de un ratchet que
    /// solo mirase texto SQL.
    /// </summary>
    private static readonly Regex EjecucionProgramatica = new(
        @"\b(NpgsqlCommand|NpgsqlConnection|ExecuteNonQuery|ExecuteScalar|DbCommand)\b",
        RegexOptions.Compiled);

    [Fact]
    public void Ninguna_migracion_crea_ni_destruye_roles_de_cluster()
    {
        var infractoras = ArchivosDeMigraciones()
            .Where(a => Prohibido.IsMatch(SinComentarios(a.Texto)))
            .Select(a => a.Ruta)
            .OrderBy(r => r)
            .ToList();

        infractoras.Should().BeEmpty(
            "los roles son objetos de clúster y los provee deploy/bootstrap/roles-de-cluster.sql " +
            "antes de que arranque ningún migrador; crearlos desde la migración de una base es lo " +
            "que producía el 42704 intermitente");
    }

    [Fact]
    public void Ninguna_migracion_ejecuta_SQL_por_su_cuenta()
    {
        var infractoras = ArchivosDeMigraciones()
            .Where(a => EjecucionProgramatica.IsMatch(SinComentarios(a.Texto)))
            .Select(a => a.Ruta)
            .OrderBy(r => r)
            .ToList();

        infractoras.Should().BeEmpty(
            "una migración que abriera su propia conexión podría crear un rol sin que el texto SQL " +
            "de migrationBuilder.Sql lo delatara");
    }

    /// <summary>
    /// El fichero de bootstrap no es un cajón de "roles varios". No congela la
    /// lista para siempre: congela que ampliarla sea un acto deliberado que pasa
    /// por este test y por su revisión.
    /// </summary>
    [Fact]
    public void El_bootstrap_declara_exactamente_los_dos_principales_del_contrato()
    {
        var guion = SinComentarios(File.ReadAllText(
            Path.Combine(RaizDelRepositorio(), "deploy", "bootstrap", "roles-de-cluster.sql")));

        var declarados = new Regex(@"CREATE\s+ROLE\s+(\w+)", RegexOptions.IgnoreCase)
            .Matches(guion)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        declarados.Should().BeEquivalentTo(["cae_app_runtime", "cae_app_soporte"]);
    }

    /// <summary>
    /// Se descartan las líneas que son íntegramente comentario —SQL con
    /// <c>--</c>, C# con <c>//</c>—, porque las migraciones explican en prosa
    /// justamente lo que este ratchet prohíbe y sin esto se denunciarían a sí
    /// mismas. Un comentario al final de una línea con código sí cuenta: para
    /// una prohibición, equivocarse hacia detectar de más obliga a mirar, que es
    /// el lado seguro.
    /// </summary>
    private static string SinComentarios(string texto) =>
        string.Join('\n', texto.Split('\n')
            .Where(l =>
            {
                var t = l.TrimStart();
                return !t.StartsWith("--", StringComparison.Ordinal)
                       && !t.StartsWith("//", StringComparison.Ordinal)
                       && !t.StartsWith("///", StringComparison.Ordinal)
                       && !t.StartsWith("*", StringComparison.Ordinal);
            }));

    private static IEnumerable<(string Ruta, string Texto)> ArchivosDeMigraciones()
    {
        var raiz = RaizDelRepositorio();
        var migraciones = Path.Combine(raiz, "src", "CaeManager.Migrations.PostgreSQL");

        return Directory
            .EnumerateFiles(migraciones, "*.cs", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !a.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(a => (
                Ruta: Path.GetRelativePath(raiz, a).Replace(Path.DirectorySeparatorChar, '/'),
                Texto: File.ReadAllText(a)));
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);

        return actual.FullName;
    }
}
