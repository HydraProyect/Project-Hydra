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
            .Where(a => Prohibido.IsMatch(ConLiteralesUnidos(SinComentarios(a.Texto))))
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
            .Where(a => EjecucionProgramatica.IsMatch(ConLiteralesUnidos(SinComentarios(a.Texto))))
            .Select(a => a.Ruta)
            .OrderBy(r => r)
            .ToList();

        infractoras.Should().BeEmpty(
            "una migración que abriera su propia conexión podría crear un rol sin que el texto SQL " +
            "de migrationBuilder.Sql lo delatara");
    }

    /// <summary>
    /// <b>El guion no construye DDL dinámicamente</b>, que es la condición sin la
    /// cual enumerar sus principales leyendo el texto no significa nada.
    ///
    /// <para>
    /// Demostrado por mutación: un tercer rol añadido dentro de un bloque
    /// <c>DO</c> con
    /// </para>
    /// <code>
    /// EXECUTE format('CREATE ROLE %I NOLOGIN NOSUPERUSER', 'cae_app_mutacion');
    /// </code>
    /// <para>
    /// no lo ve el patrón de enumeración —tras <c>ROLE</c> viene <c>%I</c>, que
    /// no casa con <c>\w+</c>—, así que el rol no entraba en la lista de
    /// declarados y el test seguía afirmando que hay exactamente dos. Y esto no
    /// es un fichero cualquiera: es el <b>contrato normativo</b> de qué
    /// principales existen en el clúster.
    /// </para>
    ///
    /// <para>
    /// La reparación no es perseguir formas de SQL dinámico una por una, sino
    /// prohibirlas: el guion no las necesita —hoy no tiene ni una— y sin ellas
    /// la enumeración estática vuelve a ser una lectura fiel de lo que el guion
    /// hace.
    /// </para>
    /// </summary>
    [Fact]
    public void El_bootstrap_no_construye_DDL_dinamicamente()
    {
        var guion = SinComentarios(File.ReadAllText(
            Path.Combine(RaizDelRepositorio(), "deploy", "bootstrap", "roles-de-cluster.sql")));

        new Regex(@"\bEXECUTE\b|\bformat\s*\(", RegexOptions.IgnoreCase).IsMatch(guion)
            .Should().BeFalse(
                "un CREATE ROLE construido en tiempo de ejecución no aparece en la enumeración de este " +
                "test, así que el contrato de 'exactamente dos principales' dejaría de estar comprobado " +
                "justo en el fichero que lo define");
    }

    /// <summary>
    /// El fichero de bootstrap no es un cajón de "roles varios". No congela la
    /// lista para siempre: congela que ampliarla sea un acto deliberado que pasa
    /// por este test y por su revisión.
    ///
    /// <para>
    /// Enumerar por texto solo vale mientras el texto sea estático, y eso lo
    /// sostiene <see cref="El_bootstrap_no_construye_DDL_dinamicamente"/>: los
    /// dos tests son una sola propiedad partida en dos aserciones.
    /// </para>
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
    /// <b>El bootstrap converge LOGIN solo donde es una propiedad de seguridad.</b>
    ///
    /// <para>
    /// Es la regresión concreta que este test existe para impedir, y no es
    /// hipotética: la primera versión del guion (2026-08-22) convergía
    /// <c>cae_app_runtime</c> a <c>NOLOGIN</c>, mientras producción llevaba
    /// desde el 2026-08-14 con <c>LOGIN</c> habilitado —que es lo que hace que
    /// RLS restrinja de verdad allí—. Ejecutarlo contra producción habría
    /// retirado ese <c>LOGIN</c> y dejado a la aplicación sin poder abrir su
    /// conexión restringida.
    /// </para>
    ///
    /// <para>
    /// La asimetría se afirma en <b>las dos direcciones</b>: en
    /// <c>cae_app_soporte</c>, <c>NOLOGIN</c> sí es un atributo de seguridad
    /// —ese rol solo se adopta con <c>SET ROLE</c> desde una sesión ya
    /// autenticada—, así que quitarlo "por coherencia" con el otro también pone
    /// esto rojo.
    /// </para>
    /// </summary>
    [Fact]
    public void El_bootstrap_converge_LOGIN_solo_donde_es_una_propiedad_de_seguridad()
    {
        var guion = SinComentarios(File.ReadAllText(
            Path.Combine(RaizDelRepositorio(), "deploy", "bootstrap", "roles-de-cluster.sql")));

        var convergencias = new Regex(@"ALTER\s+ROLE\s+(\w+)\s+WITH\s+([^;]*);", RegexOptions.IgnoreCase)
            .Matches(guion)
            .Select(m => (Rol: m.Groups[1].Value, Atributos: m.Groups[2].Value.ToUpperInvariant()))
            .ToList();

        // Guarda del propio instrumento: sin esto, un patrón que dejara de
        // encontrar las convergencias haría que todo lo de abajo pasara sobre un
        // conjunto vacío, y el test afirmaría algo que no ha observado.
        convergencias.Select(c => c.Rol).Should().BeEquivalentTo(
            ["cae_app_runtime", "cae_app_soporte"],
            "el guion converge exactamente los dos principales del contrato; si esto cambia, las " +
            "aserciones siguientes dejan de medir lo que dicen medir");

        convergencias.Single(c => c.Rol == "cae_app_runtime").Atributos
            .Should().NotContain("LOGIN",
                "LOGIN en cae_app_runtime es configuración de DESPLIEGUE, no un atributo de seguridad: " +
                "depende de si ese entorno provisiona una contraseña, y este guion no puede provisionarla " +
                "sin contener un secreto. Lo que no puede otorgar, tampoco debe destruir — producción " +
                "conecta con ese rol");

        convergencias.Single(c => c.Rol == "cae_app_soporte").Atributos
            .Should().Contain("NOLOGIN",
                "en cae_app_soporte NOLOGIN sí es un atributo de seguridad: nunca debe ser una identidad " +
                "de conexión, solo se adopta con SET ROLE desde una sesión ya autenticada");

        foreach (var (rol, atributos) in convergencias)
        {
            atributos.Should().Contain("NOSUPERUSER", $"{rol} nunca debe ignorar RLS por ser superusuario");
            atributos.Should().Contain("NOBYPASSRLS", $"{rol} nunca debe poder saltarse las políticas de aislamiento");
        }
    }


    /// <summary>
    /// Vuelve a unir los literales que C# parte con <c>+</c>.
    ///
    /// <para>
    /// Sin esto el ratchet se esquivaba escribiendo lo prohibido en dos trozos,
    /// y no es una hipótesis: la mutación
    /// </para>
    /// <code>
    /// migrationBuilder.Sql("CREATE " + "ROLE cae_app_mutacion NOLOGIN;");
    /// </code>
    /// <para>
    /// compila, crea un rol de clúster desde la migración de UNA base —el 42704
    /// intermitente que este test existe para impedir— y pasaba en verde,
    /// mientras la misma sentencia escrita de una pieza sí caía. El patrón
    /// vigilaba una forma de escribir, no la sentencia.
    /// </para>
    ///
    /// <para>
    /// Unir de más es el lado seguro, por el mismo motivo que se declara al
    /// tratar los comentarios: esto es una prohibición, y equivocarse hacia
    /// detectar de más obliga a mirar. Se contemplan los prefijos verbatim e
    /// interpolado, que pueden aparecer en cualquiera de los dos lados del
    /// <c>+</c>.
    /// </para>
    /// </summary>
    private static readonly Regex UnionDeLiterales = new(
        @"""\s*\+\s*[@$]*""", RegexOptions.Compiled);

    private static string ConLiteralesUnidos(string texto) =>
        UnionDeLiterales.Replace(texto, string.Empty);

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
