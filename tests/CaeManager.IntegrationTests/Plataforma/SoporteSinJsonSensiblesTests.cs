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
    [InlineData("SELECT \"ExtraccionJson\" FROM \"ExtraccionesIaCache\"")]
    [InlineData("SELECT \"Id\" FROM \"SolicitudesConexionMicrosoft365\"")]
    [InlineData("SELECT \"CodigoVerificacion\" FROM \"VerificacionesDocumentoOficial\"")]
    [InlineData("SELECT * FROM \"VerificacionesDocumentoOficial\"")]
    [InlineData("SELECT \"PayloadCrudo\" FROM \"EventosWebhook\"")]
    [InlineData("SELECT * FROM \"EventosWebhook\"")]
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
    [InlineData("SELECT \"Id\", \"HashSha256\" FROM \"ExtraccionesIaCache\"")]
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

    /// <summary>Contra el catálogo: una columna que falte en el GRANT dejaría en 42501 a las proyecciones.</summary>
    [Theory]
    [InlineData("VerificacionesDocumentoOficial", "CodigoVerificacion")]
    [InlineData("EventosWebhook", "PayloadCrudo")]
    [InlineData("LotesGeneracionDocumento", "ContextoJson")]
    [InlineData("ExtraccionesIaCache", "ExtraccionJson")]
    public async Task Soporte_lee_todas_las_columnas_salvo_la_retirada(string tabla, string retirada)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var consulta = new NpgsqlCommand(
            "SELECT a.attname, has_column_privilege('cae_app_soporte', a.attrelid, a.attnum, 'SELECT') " +
            "FROM pg_attribute a " +
            "WHERE a.attrelid = format('public.%I', @tabla::text)::regclass AND a.attnum > 0 AND NOT a.attisdropped",
            conexion);
        consulta.Parameters.AddWithValue("tabla", tabla);

        var todas = new List<string>();
        var legibles = new List<string>();
        await using var lector = await consulta.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            todas.Add(lector.GetString(0));
            if (lector.GetBoolean(1))
                legibles.Add(lector.GetString(0));
        }

        todas.Should().Contain(retirada, "control del instrumento: la columna existe");
        legibles.Should().BeEquivalentTo(todas.Where(c => c != retirada));
    }
}
