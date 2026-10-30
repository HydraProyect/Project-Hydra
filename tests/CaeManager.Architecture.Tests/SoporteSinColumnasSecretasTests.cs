using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Ninguna migración concede a <c>cae_app_soporte</c> las columnas secretas</b> (OWASP ASVS 4.0.3
/// V4.1.3 y V2.10.4).
///
/// <para>
/// <c>cae_app_soporte</c> es el rol de la conexión en Sesión Privilegiada. La migración
/// <c>SoporteSinLecturaDeCredenciales</c> le retiró todo privilegio sobre <c>AspNetUserTokens</c> (clave
/// TOTP, hashes de los códigos de recuperación) y <c>CredencialesIntegracion</c> (tokens de Microsoft
/// 365), y sobre <c>ClavesApi</c> le dejó solo las columnas sin <c>HashClave</c>. Este test mira el texto
/// de las migraciones posteriores para que ninguna lo deshaga: un <c>GRANT</c> sobre esas dos tablas, un
/// <c>GRANT</c> de tabla entera sobre <c>ClavesApi</c>, uno de columnas que incluya el hash, o un
/// <c>GRANT … ON ALL TABLES IN SCHEMA</c>, que lo devolvería todo de golpe.
/// </para>
///
/// <para>
/// <c>SoporteSinColumnasSecretasRestantes</c> hizo lo mismo con <c>Contrasena</c> de
/// <c>CanalesGestionDocumental</c>, <c>CredencialesAccesoEmpresa</c> y <c>CredencialesAccesoSubcontrata</c>,
/// <c>ClientState</c> de <c>SuscripcionesWebhook</c>, <c>TokenAcceso</c> de <c>LineasWhatsApp</c> y
/// <c>UltimoError</c> de <c>ConexionesIntegracion</c>; <c>AccesosPlataformaCaeEmpresa</c> ya no se concedía.
/// <c>AspNetUsers</c> no está: <c>UserManager</c> materializa la cuenta entera en Sesión Privilegiada.
/// </para>
///
/// <para>
/// Solo cuenta el <c>Up</c>: el <c>Down</c> de la propia migración vuelve a conceder lo que retiró, que es
/// lo que debe hacer. Las migraciones anteriores (la línea base) quedan cubiertas porque la que retira va
/// después; que esa migración siga retirando lo comprueba el segundo test. Lo que hay en la base migrada,
/// columna a columna, lo mide <c>SoporteSinLecturaDeCredencialesTests</c> en integración.
/// </para>
/// </summary>
public class SoporteSinColumnasSecretasTests
{
    private const string MigracionQueRetira = "20261030120000_SoporteSinLecturaDeCredenciales";

    private const string MigracionQueRetiraElResto = "20261030200000_SoporteSinColumnasSecretasRestantes";

    private static readonly string[] TablasSinNingunaColumna = ["AspNetUserTokens", "CredencialesIntegracion"];

    /// <summary>
    /// Tablas que Soporte TALVEG lee por columnas, con la columna que nunca se le concede. Las de
    /// <c>SoporteSinColumnasSecretasRestantes</c> y <c>AccesosPlataformaCaeEmpresa</c>, que
    /// <c>AnadePlataformaCaeDeClienteEmpresarial</c> ya le retiró entera.
    /// </summary>
    private static readonly Dictionary<string, string[]> ColumnasSecretasPorTabla = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ClavesApi"] = ["HashClave"],
        ["CanalesGestionDocumental"] = ["Contrasena"],
        ["CredencialesAccesoEmpresa"] = ["Contrasena"],
        ["CredencialesAccesoSubcontrata"] = ["Contrasena"],
        ["AccesosPlataformaCaeEmpresa"] = ["Contrasena"],
        ["SuscripcionesWebhook"] = ["ClientState"],
        ["LineasWhatsApp"] = ["TokenAcceso"],
        ["ConexionesIntegracion"] = ["UltimoError"],
    };

    /// <summary>Lo que retira <see cref="MigracionQueRetiraElResto"/>: grant de tabla fuera, columnas sin el secreto.</summary>
    private static readonly string[] TablasQueRetiraElResto =
    [
        "CanalesGestionDocumental", "CredencialesAccesoEmpresa", "CredencialesAccesoSubcontrata",
        "SuscripcionesWebhook", "LineasWhatsApp", "ConexionesIntegracion",
    ];

    /// <summary>
    /// Una sentencia <c>GRANT … ON … TO …</c>, terminada en <c>;</c> o en el final del literal C# que la
    /// contiene (un <c>migrationBuilder.Sql("GRANT …")</c> sin punto y coma también cuenta).
    /// </summary>
    private static readonly Regex Grant = new(
        @"\bGRANT\s+(?<privilegios>[^;]+?)\s+ON\s+(?<objeto>[^;]+?)\s+TO\s+(?<destino>[^;""]+?)\s*(?:;|""|$)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Objetos de un GRANT que no son tablas: no pueden conceder columnas.</summary>
    private static readonly Regex ObjetoQueNoEsTabla = new(
        @"^(FUNCTION|PROCEDURE|ROUTINE|SCHEMA|SEQUENCE|DATABASE|TYPE|DOMAIN|LANGUAGE|ALL\s+(FUNCTIONS|PROCEDURES|ROUTINES|SEQUENCES))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Theory]
    [InlineData("""GRANT SELECT ON TABLE public."AspNetUserTokens" TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ("UserId") ON TABLE public."AspNetUserTokens" TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ON TABLE public."CredencialesIntegracion" TO cae_app_runtime, cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ON TABLE public."ClavesApi" TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ("Id", "HashClave") ON TABLE public."ClavesApi" TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ON ALL TABLES IN SCHEMA public TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ON TABLE public."AspNetUserTokens" TO PUBLIC;""", true)]
    [InlineData("""GRANT SELECT ON TABLE public."AspNetUserTokens" TO cae_app_soporte""", true)]
    [InlineData("""GRANT SELECT ON TABLE public.\"ClavesApi\" TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ON TABLE public."{tabla}" TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ON TABLE public."Empresas", public."ClavesApi" TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ("Id", "NombreDescriptivo") ON TABLE public."ClavesApi" TO cae_app_soporte;""", false)]
    [InlineData("""GRANT SELECT ON TABLE public."Empresas" TO cae_app_soporte;""", false)]
    [InlineData("""GRANT SELECT ON TABLE public."AspNetUserTokens" TO cae_app_runtime;""", false)]
    [InlineData("""GRANT EXECUTE ON FUNCTION public.app_sesion_vigente(uuid, uuid) TO cae_app_runtime, cae_app_soporte;""", false)]
    [InlineData("""GRANT cae_app_soporte TO cae_app_runtime;""", false)]
    [InlineData("""GRANT SELECT ON TABLE public."CanalesGestionDocumental" TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ("Id", "Contrasena") ON TABLE public."CredencialesAccesoEmpresa" TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ("Id", "Contrasena") ON TABLE public."CredencialesAccesoSubcontrata" TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ON public."AccesosPlataformaCaeEmpresa" TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ("Id", "ClientState") ON TABLE public."SuscripcionesWebhook" TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ("Id", "TokenAcceso") ON TABLE public."LineasWhatsApp" TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ON TABLE public."ConexionesIntegracion" TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ("Id", "UltimoError") ON TABLE public."ConexionesIntegracion" TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ("Id", "Nombre", "Estado") ON TABLE public."ConexionesIntegracion" TO cae_app_soporte;""", false)]
    [InlineData("""GRANT SELECT ("Id", "PhoneNumberId") ON TABLE public."LineasWhatsApp" TO cae_app_soporte;""", false)]
    [InlineData("""GRANT SELECT ("Id"), SELECT ("Contrasena") ON TABLE public."CanalesGestionDocumental" TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ("Id"), UPDATE ON TABLE public."CanalesGestionDocumental" TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ("Id"), REFERENCES ("Usuario") ON TABLE public."CanalesGestionDocumental" TO cae_app_soporte;""", false)]
    [InlineData("""GRANT SELECT ("Id", "ContrasenaCambiadaEnUtc") ON TABLE public."AccesosPlataformaCaeEmpresa" TO cae_app_soporte;""", false)]
    public void El_detector_reconoce_las_concesiones_prohibidas(string sql, bool infractora)
    {
        // Sin esto, un detector que no marcara nada dejaría el test principal en verde.
        ConcesionesASoporteEn("prueba", sql).Any(EsInfractora).Should().Be(infractora, sql);
    }

    [Fact]
    public void Ninguna_migracion_posterior_concede_a_soporte_las_columnas_secretas()
    {
        var concesiones = ConcesionesASoporte().ToList();

        // Guarda del propio instrumento: la línea base concede SELECT a soporte sobre cientos de
        // tablas, entre ellas las tres. Si el patrón no las encontrara, la aserción de abajo pasaría
        // sobre un conjunto vacío.
        concesiones.Where(c => string.CompareOrdinal(c.Migracion, MigracionQueRetira) < 0)
            .Select(c => TablaDe(c.Objeto))
            .Should().Contain(["AspNetUserTokens", "CredencialesIntegracion", "ClavesApi", .. TablasQueRetiraElResto]);

        var infractoras = concesiones
            .Where(c => string.CompareOrdinal(c.Migracion, MigracionQueRetira) >= 0)
            .Where(EsInfractora)
            .Select(c => $"{c.Migracion}: GRANT {c.Privilegios} ON {c.Objeto}")
            .ToList();

        infractoras.Should().BeEmpty(
            "Soporte TALVEG no lee tokens de Identity, tokens de integraciones, hashes de claves API, " +
            "contraseñas de plataformas y portales, el ClientState de los webhooks, el token de WhatsApp " +
            "ni el último error de las conexiones; " +
            "si una lectura de Sesión Privilegiada necesita una columna de esas tablas, se concede por columna " +
            "y nunca la secreta");
    }

    [Fact]
    public void La_migracion_que_retira_sigue_retirando_las_tres_tablas()
    {
        var up = Up(File.ReadAllText(Path.Combine(DirectorioMigraciones(), MigracionQueRetira + ".cs")));

        foreach (var tabla in new[] { "AspNetUserTokens", "CredencialesIntegracion", "ClavesApi" })
            Regex.IsMatch(up, $@"REVOKE\s+ALL\s+ON\s+TABLE\s+public\.""{tabla}""\s+FROM\s+cae_app_soporte\s*;", RegexOptions.IgnoreCase)
                .Should().BeTrue($"el Up de {MigracionQueRetira} retira todo privilegio de cae_app_soporte sobre {tabla}");
    }

    [Fact]
    public void La_migracion_que_retira_el_resto_retira_el_grant_de_tabla_y_concede_columnas_sin_el_secreto()
    {
        var up = Up(File.ReadAllText(Path.Combine(DirectorioMigraciones(), MigracionQueRetiraElResto + ".cs")));
        var concesiones = ConcesionesASoporteEn(MigracionQueRetiraElResto, up).ToList();

        foreach (var tabla in TablasQueRetiraElResto)
        {
            Regex.IsMatch(up, $@"REVOKE\s+ALL\s+ON\s+TABLE\s+public\.""{tabla}""\s+FROM\s+cae_app_soporte\s*;", RegexOptions.IgnoreCase)
                .Should().BeTrue($"el Up de {MigracionQueRetiraElResto} retira el grant de tabla de cae_app_soporte sobre {tabla}");

            // Control positivo: la tabla se sigue leyendo por columnas (las consultas de Sesión
            // Privilegiada proyectan), no se queda ciega.
            concesiones.Should().Contain(c => TablaDe(c.Objeto) == tabla && c.Privilegios.StartsWith("SELECT (", StringComparison.Ordinal),
                $"{tabla} conserva SELECT por columnas para las proyecciones de Sesión Privilegiada");
        }

        concesiones.Where(EsInfractora).Should().BeEmpty();
    }

    private static bool EsInfractora((string Migracion, string Privilegios, string Objeto) concesion)
    {
        if (Regex.IsMatch(concesion.Objeto, @"\bALL\s+TABLES\b", RegexOptions.IgnoreCase))
            return true;

        if (ObjetoQueNoEsTabla.IsMatch(concesion.Objeto))
            return false;

        // Una tabla que no se puede leer del texto (interpolada, o varias en la misma sentencia) no se
        // puede comprobar: se marca, y quien la escriba la nombra de forma literal.
        var tabla = TablaDe(concesion.Objeto);
        if (tabla.Length == 0)
            return true;

        if (TablasSinNingunaColumna.Contains(tabla, StringComparer.OrdinalIgnoreCase))
            return true;

        if (!ColumnasSecretasPorTabla.TryGetValue(tabla, out var secretas))
            return false;

        // Un privilegio sin lista de columnas es de tabla entera, secreto incluido. Con varios
        // privilegios («SELECT ("Id"), SELECT ("Contrasena")») cuenta cada lista, y uno sin lista
        // («SELECT ("Id"), UPDATE») alcanza a la tabla entera.
        var privilegios = Regex.Split(concesion.Privilegios, @",(?![^(]*\))");
        if (privilegios.Any(p => !p.Contains('(')))
            return true;

        // Por nombre entre comillas o suelto: "Contrasena" no debe casar con "ContrasenaCambiadaEnUtc".
        var nombres = Regex.Matches(concesion.Privilegios, @"\((?<columnas>[^)]*)\)")
            .SelectMany(m => m.Groups["columnas"].Value.Split(','))
            .Select(c => c.Trim().Trim('"'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return secretas.Any(nombres.Contains);
    }

    private static IEnumerable<(string Migracion, string Privilegios, string Objeto)> ConcesionesASoporte()
    {
        foreach (var archivo in Directory.EnumerateFiles(DirectorioMigraciones(), "*.cs")
                     .Where(a => !a.EndsWith(".Designer.cs", StringComparison.Ordinal)
                                 && !Path.GetFileName(a).StartsWith("CaeManagerDbContextModelSnapshot", StringComparison.Ordinal))
                     .OrderBy(a => Path.GetFileName(a), StringComparer.Ordinal))
        {
            var migracion = Path.GetFileName(archivo).Split('.')[0];
            foreach (var concesion in ConcesionesASoporteEn(migracion, Up(SinComentarios(File.ReadAllText(archivo)))))
                yield return concesion;
        }
    }

    /// <summary>
    /// Las concesiones de un texto que alcanzan a <c>cae_app_soporte</c>: nombrado, o a través de
    /// <c>PUBLIC</c>, del que es miembro todo rol.
    /// </summary>
    private static IEnumerable<(string Migracion, string Privilegios, string Objeto)> ConcesionesASoporteEn(
        string migracion, string texto)
    {
        // Las comillas escapadas de un literal C# normal (\") se leen como comillas.
        foreach (Match m in Grant.Matches(texto.Replace("\\\"", "\"")))
        {
            if (!Regex.IsMatch(m.Groups["destino"].Value, @"\b(cae_app_soporte|PUBLIC)\b", RegexOptions.IgnoreCase))
                continue;

            yield return (migracion, Normalizar(m.Groups["privilegios"].Value), Normalizar(m.Groups["objeto"].Value));
        }
    }

    /// <summary>El nombre de tabla sin esquema ni comillas; vacío si el objeto no es una tabla.</summary>
    private static string TablaDe(string objeto)
    {
        var m = Regex.Match(objeto, @"^(?:TABLE\s+)?(?:public\.)?""?(?<tabla>[A-Za-z0-9_]+)""?$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["tabla"].Value : "";
    }

    /// <summary>Lo anterior al <c>Down</c>: en la línea base, todo el fichero.</summary>
    private static string Up(string texto)
    {
        var indice = texto.IndexOf("void Down(", StringComparison.Ordinal);
        return indice < 0 ? texto : texto[..indice];
    }

    private static string Normalizar(string texto) => Regex.Replace(texto, @"\s+", " ").Trim();

    /// <summary>
    /// Fuera las líneas que son íntegramente comentario (SQL <c>--</c>, C# <c>//</c> y <c>///</c>): los
    /// resúmenes de las migraciones nombran en prosa justamente las sentencias que este test vigila.
    /// </summary>
    private static string SinComentarios(string texto) =>
        string.Join('\n', texto.Split('\n')
            .Where(l =>
            {
                var t = l.TrimStart();
                return !t.StartsWith("--", StringComparison.Ordinal) && !t.StartsWith("//", StringComparison.Ordinal);
            }));

    private static string DirectorioMigraciones() =>
        Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Migrations.PostgreSQL", "Migrations");

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
