using FluentAssertions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.CalendarioLaboral;

/// <summary>
/// Las dos tablas de las capas de /calendario bajo los roles reales.
/// <list type="bullet">
/// <item><c>CalendariosLaboralesPersona</c> es dato del Tenant del Operador CAE: RLS forzada con
/// <c>aislamiento_tenant</c>; <c>cae_app_runtime</c> lee, inserta y cambia la ubicación y el interruptor de la
/// intensiva solo en su Tenant, sin borrar ni cambiar de quién es la fila; <c>cae_app_soporte</c> no la ve.</item>
/// <item><c>FestivosOficiales</c> es un catálogo global sembrado por la migración: sin RLS ni <c>TenantId</c>, el
/// runtime lo lee entero desde cualquier Tenant y no lo escribe.</item>
/// </list>
/// </summary>
public class CalendarioLaboralBajoRlsTests : IAsyncLifetime
{
    private const string Runtime = "cae_app_runtime";
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        foreach (var tenant in new[] { _tenantA, _tenantB })
        {
            await using var conexion = await AbrirComoAsync("cae_app_runtime", tenant);
            await EjecutarAsync(conexion, InsertarCalendario(tenant));
        }
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_calendario_de_la_persona_tiene_RLS_forzada_y_solo_la_politica_de_aislamiento()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT c.relrowsecurity AND c.relforcerowsecurity,
                   (SELECT count(*) FROM pg_policies p WHERE p.tablename = c.relname),
                   (SELECT count(*) FROM pg_policies p
                     WHERE p.tablename = c.relname AND p.policyname = 'aislamiento_tenant' AND p.with_check IS NOT NULL)
            FROM pg_class c
            WHERE c.oid = 'public."CalendariosLaboralesPersona"'::regclass;
            """;
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();

        lector.GetBoolean(0).Should().BeTrue("ENABLE y FORCE ROW LEVEL SECURITY");
        lector.GetInt64(1).Should().Be(1, "ninguna política permisiva de más que la ensanche");
        lector.GetInt64(2).Should().Be(1, "la política de aislamiento con WITH CHECK explícito");
    }

    [Fact]
    public async Task Runtime_solo_ve_los_calendarios_de_su_Tenant()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);

        (await ContarAsync(conexion, "CalendariosLaboralesPersona")).Should().Be(1);
        (await ContarAsync(conexion, "CalendariosLaboralesPersona", $"WHERE \"TenantId\" = '{_tenantB}'")).Should().Be(0);
    }

    [Fact]
    public async Task Runtime_sin_Tenant_ni_ve_ni_escribe_calendarios()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", tenantId: null);

        (await ContarAsync(conexion, "CalendariosLaboralesPersona")).Should().Be(0);
        await DebeDenegarseAsync(conexion, InsertarCalendario(_tenantA));
    }

    [Fact]
    public async Task Runtime_no_inserta_un_calendario_en_otro_Tenant()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);

        await DebeDenegarseAsync(conexion, InsertarCalendario(_tenantB));
    }

    [Fact]
    public async Task Runtime_cambia_la_ubicacion_de_su_Tenant_y_no_alcanza_la_de_otro()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);

        (await EjecutarAsync(conexion, """UPDATE "CalendariosLaboralesPersona" SET "Comunidad" = 9, "Municipio" = 'Barcelona', "AplicaJornadaIntensiva" = false;"""))
            .Should().Be(1);
        (await EjecutarAsync(conexion, $"""UPDATE "CalendariosLaboralesPersona" SET "Comunidad" = 16 WHERE "TenantId" = '{_tenantB}';"""))
            .Should().Be(0);
    }

    [Theory]
    [InlineData("\"Id\" = gen_random_uuid()")]
    [InlineData("\"TenantId\" = gen_random_uuid()")]
    [InlineData("\"UsuarioId\" = gen_random_uuid()")]
    public async Task Runtime_no_cambia_de_quien_es_un_calendario(string asignacion)
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);

        await DebeDenegarseAsync(conexion, $"UPDATE \"CalendariosLaboralesPersona\" SET {asignacion};");
    }

    [Fact]
    public async Task Runtime_no_borra_calendarios()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);

        await DebeDenegarseAsync(conexion, "DELETE FROM \"CalendariosLaboralesPersona\";");
    }

    [Theory]
    [InlineData("CalendariosLaboralesPersona")]
    [InlineData("FestivosOficiales")]
    public async Task Soporte_no_lee_las_tablas_del_calendario_laboral(string tabla)
    {
        await using var conexion = await AbrirComoAsync("cae_app_soporte", _tenantA);

        await DebeDenegarseAsync(conexion, $"SELECT count(*) FROM \"{tabla}\";");
    }

    [Theory]
    [InlineData("SELECT count(*) FROM \"CalendariosLaboralesPersona\";")]
    [InlineData("SELECT count(*) FROM \"FestivosOficiales\";")]
    [InlineData("UPDATE \"CalendariosLaboralesPersona\" SET \"Comunidad\" = 9;")]
    [InlineData("DELETE FROM \"CalendariosLaboralesPersona\";")]
    [InlineData("""INSERT INTO "FestivosOficiales" ("Id", "Fecha", "Nombre", "Ambito") VALUES (gen_random_uuid(), DATE '2026-11-10', 'Inventado', 1);""")]
    public async Task Aprovisionamiento_ni_lee_ni_escribe_las_tablas_del_calendario_laboral(string sql)
    {
        await using var conexion = await AbrirComoAsync("cae_app_aprovisionamiento", _tenantA);

        await DebeDenegarseAsync(conexion, sql);
    }

    [Fact]
    public async Task Aprovisionamiento_no_inserta_un_calendario()
    {
        await using var conexion = await AbrirComoAsync("cae_app_aprovisionamiento", _tenantA);

        await DebeDenegarseAsync(conexion, InsertarCalendario(_tenantA));
    }

    /// <summary>
    /// El catálogo de privilegios, no solo los intentos: lo que cada rol de la aplicación (y PUBLIC) tiene concedido sobre
    /// las dos tablas, por tabla y por columna. Una concesión de más —un <c>UPDATE</c> de tabla entera, que alcanzaría
    /// <c>Id</c>, <c>TenantId</c> y <c>UsuarioId</c>, o cualquier privilegio de soporte o de aprovisionamiento— aparece aquí
    /// aunque ningún test la intente.
    /// </summary>
    [Fact]
    public async Task Cada_rol_tiene_exactamente_los_privilegios_que_concede_la_migracion()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        var concedidos = new List<string>();

        foreach (var rol in new[] { Runtime, "cae_app_soporte", "cae_app_aprovisionamiento", "public" })
        {
            foreach (var tabla in new[] { "CalendariosLaboralesPersona", "FestivosOficiales" })
            {
                foreach (var privilegio in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER" })
                {
                    await using var deTabla = conexion.CreateCommand();
                    deTabla.CommandText = "SELECT has_table_privilege(@rol, format('public.%I', @tabla::text), @privilegio);";
                    deTabla.Parameters.AddWithValue("rol", rol);
                    deTabla.Parameters.AddWithValue("tabla", tabla);
                    deTabla.Parameters.AddWithValue("privilegio", privilegio);
                    if ((bool)(await deTabla.ExecuteScalarAsync())!)
                    {
                        concedidos.Add($"{rol} {tabla} {privilegio}");
                        continue;
                    }

                    if (privilegio is "DELETE" or "TRUNCATE" or "TRIGGER")
                        continue;

                    // Sin el privilegio de tabla: las columnas sueltas en las que lo tiene.
                    await using var deColumnas = conexion.CreateCommand();
                    deColumnas.CommandText = """
                        SELECT column_name::text FROM information_schema.columns
                        WHERE table_schema = 'public' AND table_name = @tabla
                          AND has_column_privilege(@rol, format('public.%I', @tabla::text), column_name, @privilegio)
                        ORDER BY column_name;
                        """;
                    deColumnas.Parameters.AddWithValue("rol", rol);
                    deColumnas.Parameters.AddWithValue("tabla", tabla);
                    deColumnas.Parameters.AddWithValue("privilegio", privilegio);
                    await using var lector = await deColumnas.ExecuteReaderAsync();
                    while (await lector.ReadAsync())
                        concedidos.Add($"{rol} {tabla} {privilegio}({lector.GetString(0)})");
                }
            }
        }

        concedidos.Should().BeEquivalentTo(
            [
                $"{Runtime} CalendariosLaboralesPersona SELECT",
                $"{Runtime} CalendariosLaboralesPersona INSERT",
                $"{Runtime} CalendariosLaboralesPersona UPDATE(AplicaJornadaIntensiva)",
                $"{Runtime} CalendariosLaboralesPersona UPDATE(Comunidad)",
                $"{Runtime} CalendariosLaboralesPersona UPDATE(Municipio)",
                $"{Runtime} FestivosOficiales SELECT",
            ],
            "el runtime solo cambia la ubicación y el interruptor de la intensiva, nunca Id, TenantId ni UsuarioId; " +
            "soporte, aprovisionamiento y PUBLIC no tienen nada");
    }

    [Fact]
    public async Task El_catalogo_de_festivos_es_global_y_se_lee_entero_desde_cualquier_Tenant()
    {
        await using var desdeA = await AbrirComoAsync("cae_app_runtime", _tenantA);
        await using var desdeB = await AbrirComoAsync("cae_app_runtime", _tenantB);

        var totalA = await ContarAsync(desdeA, "FestivosOficiales");
        totalA.Should().Be(45, "8 + 16 en 2026 y 9 + 12 en 2027: los nacionales y los de las cuatro comunidades cubiertas");
        (await ContarAsync(desdeB, "FestivosOficiales")).Should().Be(totalA);
    }

    [Fact]
    public async Task El_catalogo_de_festivos_no_tiene_RLS_ni_TenantId()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT c.relrowsecurity,
                   (SELECT count(*) FROM information_schema.columns
                     WHERE table_name = 'FestivosOficiales' AND column_name = 'TenantId')
            FROM pg_class c
            WHERE c.oid = 'public."FestivosOficiales"'::regclass;
            """;
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();

        lector.GetBoolean(0).Should().BeFalse("es un catálogo oficial igual para todos los Tenants");
        lector.GetInt64(1).Should().Be(0);
    }

    [Theory]
    [InlineData("""INSERT INTO "FestivosOficiales" ("Id", "Fecha", "Nombre", "Ambito") VALUES (gen_random_uuid(), DATE '2026-11-10', 'Inventado', 1);""")]
    [InlineData("""UPDATE "FestivosOficiales" SET "Nombre" = 'Cambiado';""")]
    [InlineData("""DELETE FROM "FestivosOficiales";""")]
    public async Task Runtime_no_escribe_el_catalogo_de_festivos(string sql)
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);

        await DebeDenegarseAsync(conexion, sql);
    }

    private static string InsertarCalendario(Guid tenantId) => $"""
        INSERT INTO "CalendariosLaboralesPersona" ("Id", "TenantId", "UsuarioId", "Comunidad", "Municipio", "AplicaJornadaIntensiva")
        VALUES (gen_random_uuid(), '{tenantId}', gen_random_uuid(), 2, 'Zaragoza', true);
        """;

    /// <summary>
    /// Una conexión que <b>autentica como <c>cae_app_runtime</c></b> (<see cref="BaseDatosPostgresDePruebas.CadenaComoRuntime"/>),
    /// no una propietaria con <c>SET ROLE</c>. Para <c>cae_app_soporte</c> y <c>cae_app_aprovisionamiento</c> eleva desde
    /// ella con <c>SET ROLE</c>, como hace la aplicación (el runtime es miembro de los dos con INHERIT FALSE). Comprueba la
    /// identidad efectiva antes de devolverla, para que ningún test pase por haber corrido con otra.
    /// </summary>
    private async Task<NpgsqlConnection> AbrirComoAsync(string rol, Guid? tenantId)
    {
        var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion));
        await conexion.OpenAsync();

        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: tenantId);

        if (rol != Runtime)
        {
            await using var setRol = conexion.CreateCommand();
            setRol.CommandText = $"SET ROLE {rol};";
            await setRol.ExecuteNonQueryAsync();
        }

        await using var identidad = conexion.CreateCommand();
        identidad.CommandText = "SELECT session_user::text || '/' || current_user::text;";
        ((string)(await identidad.ExecuteScalarAsync())!).Should().Be($"{Runtime}/{rol}");

        return conexion;
    }

    private static async Task<int> EjecutarAsync(NpgsqlConnection conexion, string sql)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        return await comando.ExecuteNonQueryAsync();
    }

    private static async Task DebeDenegarseAsync(NpgsqlConnection conexion, string sql)
    {
        var accion = () => EjecutarAsync(conexion, sql);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, string tabla, string filtro = "")
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"SELECT count(*) FROM \"{tabla}\" {filtro};";
        return (long)(await comando.ExecuteScalarAsync())!;
    }
}
