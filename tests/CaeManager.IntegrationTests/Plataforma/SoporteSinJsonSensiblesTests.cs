using FluentAssertions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

public class SoporteSinJsonSensiblesTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public Task InitializeAsync() => BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);
    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Theory]
    [InlineData("SELECT \"DatosUtilizadosJson\" FROM \"DocumentosGenerados\"")]
    [InlineData("SELECT \"ContextoJson\" FROM \"LotesGeneracionDocumento\"")]
    [InlineData("SELECT \"Id\" FROM \"SolicitudesConexionMicrosoft365\"")]
    public async Task Soporte_no_lee_lo_retirado_y_runtime_conserva_la_lectura(string sql)
    {
        await using var runtime = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion));
        await runtime.OpenAsync();
        await ContextoRlsDePruebas.FijarAsync(runtime, tenantId: Guid.NewGuid());
        await using (var control = new NpgsqlCommand(sql, runtime))
        await using (var lector = await control.ExecuteReaderAsync()) { }

        await using var soporte = new NpgsqlConnection(_cadenaConexion);
        await soporte.OpenAsync();
        await ContextoRlsDePruebas.FijarAsync(soporte, tenantId: Guid.NewGuid());
        await using (var rol = new NpgsqlCommand("SET ROLE cae_app_soporte", soporte))
            await rol.ExecuteNonQueryAsync();
        await using var consulta = new NpgsqlCommand(sql, soporte);
        var leer = async () => await consulta.ExecuteReaderAsync();
        (await leer.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Theory]
    [InlineData("SELECT \"Id\", \"Estado\" FROM \"DocumentosGenerados\"")]
    [InlineData("SELECT \"Id\", \"Estado\", \"TotalItems\", \"ItemsCompletados\", \"ItemsFallidos\" FROM \"LotesGeneracionDocumento\"")]
    public async Task Soporte_conserva_las_columnas_necesarias(string sql)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: Guid.NewGuid());
        await using (var rol = new NpgsqlCommand("SET ROLE cae_app_soporte", conexion))
            await rol.ExecuteNonQueryAsync();
        await using var consulta = new NpgsqlCommand(sql, conexion);
        await using var lector = await consulta.ExecuteReaderAsync();
    }
}
