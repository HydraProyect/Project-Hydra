using FluentAssertions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.ApiKeys;

/// <summary>
/// <c>UsosApiDiarios</c> bajo los roles reales: <c>cae_app_runtime</c> lee, inserta y cambia solo
/// <c>Peticiones</c>, siempre en su Tenant; <c>cae_app_soporte</c> no la ve. Cada caso siembra con el propio
/// runtime la fila de cada Tenant, que es la escritura que hace la aplicación.
/// </summary>
public class UsoApiDiarioBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        foreach (var tenant in new[] { _tenantA, _tenantB })
        {
            await using var conexion = await AbrirComoAsync("cae_app_runtime", tenant);
            await EjecutarAsync(conexion, InsertarFila(tenant));
        }
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
            WHERE c.oid = 'public."UsosApiDiarios"'::regclass;
            """;
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();

        lector.GetBoolean(0).Should().BeTrue("ENABLE y FORCE ROW LEVEL SECURITY");
        lector.GetInt64(1).Should().Be(1, "la política de aislamiento con WITH CHECK explícito");
    }

    [Fact]
    public async Task Runtime_solo_ve_las_filas_de_su_Tenant()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);

        (await ContarAsync(conexion)).Should().Be(1);
        (await ContarAsync(conexion, $"WHERE \"TenantId\" = '{_tenantB}'")).Should().Be(0);
    }

    [Fact]
    public async Task Runtime_sin_Tenant_ni_ve_ni_escribe()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", tenantId: null);

        (await ContarAsync(conexion)).Should().Be(0);
        await DebeDenegarseAsync(conexion, InsertarFila(_tenantA));
    }

    [Fact]
    public async Task Runtime_no_inserta_en_otro_Tenant()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);

        await DebeDenegarseAsync(conexion, InsertarFila(_tenantB));
    }

    [Fact]
    public async Task Runtime_suma_a_las_peticiones_de_su_Tenant_y_no_alcanza_las_de_otro()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);

        (await EjecutarAsync(conexion, "UPDATE \"UsosApiDiarios\" SET \"Peticiones\" = \"Peticiones\" + 1;")).Should().Be(1);
        (await EjecutarAsync(conexion, $"UPDATE \"UsosApiDiarios\" SET \"Peticiones\" = 99 WHERE \"TenantId\" = '{_tenantB}';"))
            .Should().Be(0);
    }

    [Theory]
    [InlineData("\"TenantId\" = gen_random_uuid()")]
    [InlineData("\"ApiKeyId\" = gen_random_uuid()")]
    [InlineData("\"TipoClave\" = 1")]
    [InlineData("\"Dia\" = DATE '2026-01-01'")]
    [InlineData("\"Endpoint\" = '/otra'")]
    public async Task Runtime_no_cambia_de_quien_es_una_fila(string asignacion)
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);

        await DebeDenegarseAsync(conexion, $"UPDATE \"UsosApiDiarios\" SET {asignacion};");
    }

    [Fact]
    public async Task Runtime_no_borra_filas()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);

        await DebeDenegarseAsync(conexion, "DELETE FROM \"UsosApiDiarios\";");
    }

    [Fact]
    public async Task Soporte_no_lee_la_tabla()
    {
        await using var conexion = await AbrirComoAsync("cae_app_soporte", _tenantA);

        await DebeDenegarseAsync(conexion, "SELECT count(*) FROM \"UsosApiDiarios\";");
    }

    private static string InsertarFila(Guid tenantId) => $"""
        INSERT INTO "UsosApiDiarios" ("Id", "TenantId", "ApiKeyId", "TipoClave", "Dia", "Endpoint", "Peticiones")
        VALUES (gen_random_uuid(), '{tenantId}', gen_random_uuid(), 2, DATE '2026-11-02', '/api/v1/clientes', 1);
        """;

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

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, string filtro = "")
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"SELECT count(*) FROM \"UsosApiDiarios\" {filtro};";
        return (long)(await comando.ExecuteScalarAsync())!;
    }
}
