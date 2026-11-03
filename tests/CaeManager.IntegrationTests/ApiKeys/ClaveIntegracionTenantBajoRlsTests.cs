using CaeManager.Domain.ApiKeys;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.ApiKeys;

/// <summary>
/// La tabla de las Claves de Integración del Tenant bajo los roles reales: <c>cae_app_runtime</c>, con
/// RLS y sin borrado físico, y <c>cae_app_soporte</c>, solo metadatos y sin hash. Las sembradas son una
/// clave de cada Tenant, más una eliminada del Tenant A.
/// </summary>
public class ClaveIntegracionTenantBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly string _hashA = new('a', 64);
    private readonly string _hashB = new('b', 64);
    private readonly string _hashEliminada = new('c', 64);

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        await SembrarAsync(_tenantA, _hashA);
        await SembrarAsync(_tenantB, _hashB);
        await SembrarAsync(_tenantA, _hashEliminada, eliminada: true);
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task La_tabla_tiene_RLS_forzada_y_la_politica_de_aislamiento()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT c.relrowsecurity AND c.relforcerowsecurity,
                   (SELECT count(*) FROM pg_policies p
                     WHERE p.tablename = c.relname AND p.policyname = 'aislamiento_tenant' AND p.with_check IS NOT NULL)
            FROM pg_class c
            WHERE c.oid = 'public."ClavesIntegracionTenant"'::regclass;
            """;
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();

        lector.GetBoolean(0).Should().BeTrue("ENABLE y FORCE ROW LEVEL SECURITY");
        lector.GetInt64(1).Should().Be(1, "la política de aislamiento con WITH CHECK explícito");
    }

    [Fact]
    public async Task Runtime_solo_ve_las_claves_de_su_Tenant()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);

        (await ContarAsync(conexion)).Should().Be(2, "la vigente y la eliminada del Tenant A, nunca la del Tenant B");
        (await ContarAsync(conexion, $"WHERE \"HashClave\" = '{_hashB}'")).Should().Be(0);
    }

    [Fact]
    public async Task Runtime_sin_Tenant_no_ve_ninguna_clave()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", tenantId: null);

        (await ContarAsync(conexion)).Should().Be(0);
    }

    [Fact]
    public async Task Runtime_inserta_una_clave_en_su_Tenant()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = InsertarClave(_tenantA);

        (await comando.ExecuteNonQueryAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Runtime_no_inserta_una_clave_en_otro_Tenant()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = InsertarClave(_tenantB);

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "lo rechaza el WITH CHECK de la política");
    }

    [Fact]
    public async Task Runtime_actualiza_el_estado_de_las_claves_de_su_Tenant()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "UPDATE \"ClavesIntegracionTenant\" SET \"UltimoUsoUtc\" = now(), \"Version\" = gen_random_uuid();";

        (await comando.ExecuteNonQueryAsync()).Should().Be(2, "las dos del Tenant A, nunca la del Tenant B");
    }

    [Theory]
    [InlineData("\"HashClave\" = 'x'")]
    [InlineData("\"Alcances\" = ARRAY['documentos.leer']")]
    [InlineData("\"CreadaPorUsuarioId\" = gen_random_uuid()")]
    public async Task Runtime_no_cambia_lo_que_se_fijo_al_emitir(string asignacion)
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"UPDATE \"ClavesIntegracionTenant\" SET {asignacion};";

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Runtime_no_mueve_una_clave_a_otro_Tenant()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"UPDATE \"ClavesIntegracionTenant\" SET \"TenantId\" = '{_tenantB}';";

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Una_clave_solo_tiene_una_sucesora()
    {
        var antecesora = Guid.NewGuid();
        await SembrarAsync(_tenantA, new string('d', 64), sucedeA: antecesora);

        var accion = () => SembrarAsync(_tenantA, new string('e', 64), sucedeA: antecesora);

        (await accion.Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Declarar_como_antecesora_una_clave_ajena_no_bloquea_su_rotacion()
    {
        var antecesoraDeB = Guid.NewGuid();
        await SembrarAsync(_tenantA, new string('d', 64), sucedeA: antecesoraDeB);

        var accion = () => SembrarAsync(_tenantB, new string('e', 64), sucedeA: antecesoraDeB);

        await accion.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Runtime_no_borra_filas()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "DELETE FROM \"ClavesIntegracionTenant\";";

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task La_funcion_resuelve_el_Tenant_por_el_hash_sin_Tenant_en_el_contexto()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", tenantId: null);

        (await TenantDeAsync(conexion, _hashA)).Should().Be(_tenantA);
        (await TenantDeAsync(conexion, _hashB)).Should().Be(_tenantB);
    }

    [Fact]
    public async Task La_funcion_no_resuelve_un_hash_desconocido_ni_una_clave_eliminada()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", tenantId: null);

        (await TenantDeAsync(conexion, new string('f', 64))).Should().BeNull();
        (await TenantDeAsync(conexion, _hashEliminada)).Should().BeNull();
    }

    [Fact]
    public async Task La_funcion_no_alcanza_las_claves_de_Soporte_TALVEG()
    {
        await using var propietario = new NpgsqlConnection(_cadenaConexion);
        await propietario.OpenAsync();
        await using var definicion = propietario.CreateCommand();
        definicion.CommandText = "SELECT pg_get_functiondef('public.app_tenant_de_clave_integracion(text)'::regprocedure);";
        var cuerpo = (string)(await definicion.ExecuteScalarAsync())!;

        cuerpo.Should().Contain("\"ClavesIntegracionTenant\"").And.NotContain("\"ClavesApi\"");
    }

    /// <summary>
    /// Contrato de catálogo, el mismo que el de las demás definidoras: si se pierde el <c>search_path</c>
    /// fijado o el <c>REVOKE</c>, los tests de comportamiento seguirían en verde y cualquier rol con
    /// conexión tendría un oráculo de hash a Tenant.
    /// </summary>
    [Fact]
    public async Task La_funcion_tiene_el_contrato_de_catalogo_de_las_definidoras()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT p.prosecdef,
                   pg_get_userbyid(p.proowner),
                   array_to_string(p.proconfig, ';'),
                   pg_get_function_identity_arguments(p.oid),
                   has_function_privilege('cae_app_runtime', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_soporte', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_aprovisionamiento', p.oid, 'EXECUTE'),
                   p.proacl IS NULL OR EXISTS (SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee = 0),
                   p.provolatile
            FROM pg_proc p
            WHERE p.proname = 'app_tenant_de_clave_integracion' AND p.pronamespace = 'public'::regnamespace;
            """;
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();

        lector.GetBoolean(0).Should().BeTrue("SECURITY DEFINER");
        lector.GetString(1).Should().NotBe("cae_app_runtime");
        lector.GetString(2).Should().Be("search_path=pg_catalog, pg_temp");
        lector.GetString(3).Should().Be("hash_clave text");
        lector.GetBoolean(4).Should().BeTrue("la usa la obtención de token");
        lector.GetBoolean(5).Should().BeFalse("Soporte TALVEG no resuelve claves");
        lector.GetBoolean(6).Should().BeFalse();
        lector.GetBoolean(7).Should().BeFalse("REVOKE ALL ... FROM PUBLIC");
        lector.GetChar(8).Should().Be('s', "STABLE, solo lee");
    }

    [Fact]
    public async Task Soporte_no_resuelve_claves_con_la_funcion()
    {
        await using var conexion = await AbrirComoAsync("cae_app_soporte", _tenantA);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"SELECT app_tenant_de_clave_integracion('{_hashA}');";

        var accion = async () => await comando.ExecuteScalarAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Theory]
    [InlineData("SELECT \"HashClave\" FROM \"ClavesIntegracionTenant\";")]
    [InlineData("SELECT * FROM \"ClavesIntegracionTenant\";")]
    [InlineData("UPDATE \"ClavesIntegracionTenant\" SET \"Nombre\" = 'x';")]
    [InlineData("DELETE FROM \"ClavesIntegracionTenant\";")]
    public async Task Soporte_no_lee_el_hash_ni_escribe(string sql)
    {
        await using var conexion = await AbrirComoAsync("cae_app_soporte", _tenantA);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Soporte_lee_los_metadatos_solo_del_Tenant_de_la_sesion()
    {
        await using var conexion = await AbrirComoAsync("cae_app_soporte", _tenantA);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT count(*) FROM (SELECT "Id", "Nombre", "SistemaConsumidor", "PrefijoVisible", "Alcances", "CaducaEnUtc",
                                         "SuspendidaEnUtc", "RevocadaEnUtc", "UltimoUsoUtc"
                                  FROM "ClavesIntegracionTenant" WHERE NOT "EstaEliminado") claves;
            """;

        ((long)(await comando.ExecuteScalarAsync())!).Should().Be(1, "la vigente del Tenant A, no la del Tenant B");
    }

    private static string InsertarClave(Guid tenantId) => $"""
        INSERT INTO "ClavesIntegracionTenant"
          ("Id", "TenantId", "Nombre", "SistemaConsumidor", "PrefijoVisible", "HashClave", "Alcances", "CaducaEnUtc",
           "CreadaPorUsuarioId", "Version", "CreadoEnUtc", "EstaEliminado")
        VALUES (gen_random_uuid(), '{tenantId}', 'n', 's', 'p', '{new string('d', 64)}', ARRAY['clientes.leer'], now() + interval '1 day',
           gen_random_uuid(), gen_random_uuid(), now(), false);
        """;

    private async Task SembrarAsync(Guid tenantId, string hash, bool eliminada = false, Guid? sucedeA = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        await using var contexto = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);

        var clave = new ClaveIntegracionTenant(
            "ERP de nóminas", "ERP", "talveg_int_ab12", hash, [AlcancesIntegracion.ClientesLeer], DateTime.UtcNow.AddDays(30), Guid.NewGuid());
        if (eliminada)
            clave.MarcarComoEliminado(Guid.NewGuid());
        if (sucedeA is { } antecesora)
            contexto.Entry(clave).Property(c => c.SucedeAClaveId).CurrentValue = antecesora;
        contexto.ClavesIntegracionTenant.Add(clave);
        await contexto.SaveChangesAsync();
    }

    private async Task<NpgsqlConnection> AbrirComoAsync(string rol, Guid? tenantId)
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: tenantId);

        await using var setRol = conexion.CreateCommand();
        setRol.CommandText = $"SET ROLE {rol};";
        await setRol.ExecuteNonQueryAsync();

        return conexion;
    }

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, string filtro = "")
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"SELECT count(*) FROM \"ClavesIntegracionTenant\" {filtro};";
        return (long)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task<Guid?> TenantDeAsync(NpgsqlConnection conexion, string hash)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT app_tenant_de_clave_integracion(@hash);";
        comando.Parameters.AddWithValue("hash", hash);
        return await comando.ExecuteScalarAsync() is Guid tenant ? tenant : null;
    }
}
