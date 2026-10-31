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
/// <c>SoporteSinCodigoSeguroDocumentosGenerados</c> hizo lo mismo con <c>CodigoSeguroVerificacion</c> de
/// <c>DocumentosGenerados</c> (ASVS V4.1.3), y <c>SoporteSinSidSesionUsuario</c>, con <c>Sid</c> de
/// <c>SesionesUsuario</c> (ASVS V4.1.3).
/// </para>
///
/// <para>
/// <c>SoporteSinJsonSensibles</c> retiró <c>DatosUtilizadosJson</c>, <c>ContextoJson</c>, <c>ExtraccionJson</c>
/// y la tabla <c>SolicitudesConexionMicrosoft365</c> (ASVS V4.1.3).
/// </para>
///
/// <para>
/// <c>SoporteSinCodigoVerificacionNiPayloadWebhook</c> retiró <c>CodigoVerificacion</c> de
/// <c>VerificacionesDocumentoOficial</c> y <c>PayloadCrudo</c> de <c>EventosWebhook</c> (ASVS V4.1.3).
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

    private const string MigracionQueRetiraElCodigoSeguro = "20261030230000_SoporteSinCodigoSeguroDocumentosGenerados";

    private const string MigracionQueRetiraSid = "20261031000000_SoporteSinSidSesionUsuario";

    private const string MigracionQueRetiraLosJson = "20261031020000_SoporteSinJsonSensibles";
    private const string MigracionQueRetiraCodigoYPayload = "20261101000000_SoporteSinCodigoVerificacionNiPayloadWebhook";
    private const string MigracionQueRetiraUserLogins = "20261101004000_SoporteSinLecturaDeUserLogins";

    /// <summary>
    /// Lo que retira <see cref="MigracionQueRetiraLosJson"/>. Solo rige desde esa migración: las anteriores
    /// conceden esas columnas por lista, y esta las deshace después.
    /// </summary>
    private static readonly Dictionary<string, string> ColumnasRetiradasConLosJson = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DocumentosGenerados"] = "DatosUtilizadosJson",
        ["LotesGeneracionDocumento"] = "ContextoJson",
        ["ExtraccionesIaCache"] = "ExtraccionJson",
    };

    private static readonly string[] TablasSinNingunaColumna = ["AspNetUserTokens", "AspNetUserLogins", "CredencialesIntegracion", "CredencialesConexionPlataformaCae"];

    /// <summary>
    /// Tablas que Soporte TALVEG lee por columnas, con la columna que nunca se le concede. Las de
    /// <c>SoporteSinColumnasSecretasRestantes</c>, <c>SoporteSinCodigoSeguroDocumentosGenerados</c> y
    /// <c>SoporteSinSidSesionUsuario</c>, y <c>AccesosPlataformaCaeEmpresa</c>, que
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
        ["DocumentosGenerados"] = ["CodigoSeguroVerificacion"],
        ["SesionesUsuario"] = ["Sid"],
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
    [InlineData("""GRANT SELECT ON TABLE public."DocumentosGenerados" TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ("Id", "CodigoSeguroVerificacion") ON TABLE public."DocumentosGenerados" TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ("Id", "Estado") ON TABLE public."DocumentosGenerados" TO cae_app_soporte;""", false)]
    [InlineData("""GRANT SELECT ON TABLE public."SesionesUsuario" TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ("Id", "Sid") ON TABLE public."SesionesUsuario" TO cae_app_soporte;""", true)]
    [InlineData("""GRANT SELECT ("Id", "UsuarioId") ON TABLE public."SesionesUsuario" TO cae_app_soporte;""", false)]
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
            .Should().Contain(["AspNetUserTokens", "CredencialesIntegracion", "ClavesApi", "DocumentosGenerados", "SesionesUsuario", .. TablasQueRetiraElResto]);

        var infractoras = concesiones
            .Where(c => string.CompareOrdinal(c.Migracion, MigracionQueRetira) >= 0)
            .Where(EsInfractora)
            .Select(c => $"{c.Migracion}: GRANT {c.Privilegios} ON {c.Objeto}")
            .ToList();

        infractoras.Should().BeEmpty(
            "Soporte TALVEG no lee tokens de Identity, tokens de integraciones, hashes de claves API, " +
            "contraseñas de plataformas y portales, el ClientState de los webhooks, el token de WhatsApp, " +
            "el último error de las conexiones, el código seguro de los documentos generados ni el identificador " +
            "de las sesiones de usuario; " +
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
    public void La_migracion_de_los_logins_externos_retira_la_tabla_entera_a_soporte()
    {
        var up = Up(File.ReadAllText(Path.Combine(DirectorioMigraciones(), MigracionQueRetiraUserLogins + ".cs")));

        Regex.IsMatch(up, @"REVOKE\s+ALL\s+ON\s+TABLE\s+public\.""AspNetUserLogins""\s+FROM\s+cae_app_soporte\s*;", RegexOptions.IgnoreCase)
            .Should().BeTrue($"el Up de {MigracionQueRetiraUserLogins} retira todo privilegio de cae_app_soporte sobre AspNetUserLogins");
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

    [Fact]
    public void La_migracion_del_codigo_seguro_retira_el_grant_de_tabla_y_concede_columnas_sin_el_codigo()
    {
        var up = Up(SinComentarios(File.ReadAllText(Path.Combine(DirectorioMigraciones(), MigracionQueRetiraElCodigoSeguro + ".cs"))));
        var concesiones = ConcesionesASoporteEn(MigracionQueRetiraElCodigoSeguro, up).ToList();

        Regex.IsMatch(up, @"REVOKE\s+ALL\s+ON\s+TABLE\s+public\.""DocumentosGenerados""\s+FROM\s+cae_app_soporte\s*;", RegexOptions.IgnoreCase)
            .Should().BeTrue($"el Up de {MigracionQueRetiraElCodigoSeguro} retira el grant de tabla de cae_app_soporte");

        // Control positivo: el listado de documentos generados sigue leyendo la tabla por columnas.
        concesiones.Should().ContainSingle(c => TablaDe(c.Objeto) == "DocumentosGenerados" && c.Privilegios.StartsWith("SELECT (", StringComparison.Ordinal));
        concesiones.Where(EsInfractora).Should().BeEmpty();
    }

    [Theory]
    [InlineData("SELECT (\"Id\", \"DatosUtilizadosJson\")", "DocumentosGenerados", true)]
    [InlineData("SELECT (\"Id\", \"ContextoJson\")", "LotesGeneracionDocumento", true)]
    [InlineData("SELECT (\"Id\", \"ExtraccionJson\")", "ExtraccionesIaCache", true)]
    [InlineData("SELECT", "ExtraccionesIaCache", true)]
    [InlineData("SELECT", "SolicitudesConexionMicrosoft365", true)]
    [InlineData("SELECT (\"Id\", \"Estado\")", "LotesGeneracionDocumento", false)]
    [InlineData("SELECT (\"Id\", \"HashSha256\")", "ExtraccionesIaCache", false)]
    public void El_trinquete_detecta_las_nuevas_restricciones(string privilegios, string tabla, bool infractora) =>
        EsInfractora((MigracionQueRetiraLosJson, privilegios, $"public.\"{tabla}\""))
            .Should().Be(infractora);

    [Fact]
    public void La_migracion_de_json_retira_los_permisos_previstos()
    {
        var up = Up(SinComentarios(File.ReadAllText(Path.Combine(DirectorioMigraciones(), MigracionQueRetiraLosJson + ".cs"))));
        foreach (var tabla in new[] { "DocumentosGenerados", "LotesGeneracionDocumento", "ExtraccionesIaCache", "SolicitudesConexionMicrosoft365" })
            Regex.IsMatch(up, $@"REVOKE\s+ALL\s+ON\s+TABLE\s+public\.""{tabla}""\s+FROM\s+cae_app_soporte\s*;", RegexOptions.IgnoreCase)
                .Should().BeTrue();
        var concesiones = ConcesionesASoporteEn(MigracionQueRetiraLosJson, up).ToList();
        concesiones.Should().HaveCount(3);
        concesiones.Where(EsInfractora).Should().BeEmpty();
    }

    [Fact]
    public void La_migracion_de_sid_retira_el_grant_de_tabla_y_concede_las_demas_columnas()
    {
        var up = Up(SinComentarios(File.ReadAllText(Path.Combine(DirectorioMigraciones(), MigracionQueRetiraSid + ".cs"))));
        var concesiones = ConcesionesASoporteEn(MigracionQueRetiraSid, up).ToList();
        Regex.IsMatch(up, @"REVOKE\s+ALL\s+ON\s+TABLE\s+public\.""SesionesUsuario""\s+FROM\s+cae_app_soporte\s*;", RegexOptions.IgnoreCase)
            .Should().BeTrue();
        concesiones.Should().ContainSingle(c => TablaDe(c.Objeto) == "SesionesUsuario" && c.Privilegios.StartsWith("SELECT (", StringComparison.Ordinal));
        concesiones.Where(EsInfractora).Should().BeEmpty();
    }

    [Theory]
    [InlineData("VerificacionesDocumentoOficial", "CodigoVerificacion")]
    [InlineData("EventosWebhook", "PayloadCrudo")]
    public void El_detector_impide_reconceder_las_nuevas_columnas(string tabla, string columna)
    {
        EsInfractora((MigracionQueRetiraCodigoYPayload, "SELECT", $"public.\"{tabla}\"")).Should().BeTrue();
        EsInfractora((MigracionQueRetiraCodigoYPayload, $"SELECT (\"Id\", \"{columna}\")", $"public.\"{tabla}\"")).Should().BeTrue();
        EsInfractora((MigracionQueRetiraCodigoYPayload, "SELECT (\"Id\")", $"public.\"{tabla}\"")).Should().BeFalse();
    }

    [Fact]
    public void La_migracion_de_codigo_y_payload_retira_el_grant_de_tabla_y_concede_por_columnas()
    {
        var up = Up(SinComentarios(File.ReadAllText(Path.Combine(DirectorioMigraciones(), MigracionQueRetiraCodigoYPayload + ".cs"))));
        foreach (var tabla in new[] { "VerificacionesDocumentoOficial", "EventosWebhook" })
            Regex.IsMatch(up, $@"REVOKE\s+ALL\s+ON\s+TABLE\s+public\.""{tabla}""\s+FROM\s+cae_app_soporte\s*;", RegexOptions.IgnoreCase)
                .Should().BeTrue();
        var concesiones = ConcesionesASoporteEn(MigracionQueRetiraCodigoYPayload, up).ToList();
        concesiones.Should().HaveCount(2);
        concesiones.Where(EsInfractora).Should().BeEmpty();
    }

    private static bool EsInfractora((string Migracion, string Privilegios, string Objeto) concesion)
    {
        if (string.CompareOrdinal(concesion.Migracion, MigracionQueRetiraCodigoYPayload) >= 0)
        {
            var tablaCodigo = TablaDe(concesion.Objeto);
            var columna = tablaCodigo.Equals("VerificacionesDocumentoOficial", StringComparison.OrdinalIgnoreCase)
                ? "CodigoVerificacion"
                : tablaCodigo.Equals("EventosWebhook", StringComparison.OrdinalIgnoreCase) ? "PayloadCrudo" : null;
            if (columna is not null)
            {
                var listas = Regex.Matches(concesion.Privilegios, @"\((?<columnas>[^)]*)\)");
                if (listas.Count == 0 || Regex.Split(concesion.Privilegios, @",(?![^(]*\))").Any(p => !p.Contains('('))
                    || listas.SelectMany(m => m.Groups["columnas"].Value.Split(',')).Any(c => c.Trim().Trim('"').Equals(columna, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        if (string.CompareOrdinal(concesion.Migracion, MigracionQueRetiraLosJson) >= 0)
        {
            var tablaNueva = TablaDe(concesion.Objeto);
            if (tablaNueva.Equals("SolicitudesConexionMicrosoft365", StringComparison.OrdinalIgnoreCase)) return true;
            if (ColumnasRetiradasConLosJson.TryGetValue(tablaNueva, out var columna))
            {
                var listas = Regex.Matches(concesion.Privilegios, @"\((?<columnas>[^)]*)\)");
                if (listas.Count == 0 || Regex.Split(concesion.Privilegios, @",(?![^(]*\))").Any(p => !p.Contains('('))
                    || listas.SelectMany(m => m.Groups["columnas"].Value.Split(',')).Any(c => c.Trim().Trim('"').Equals(columna, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
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
