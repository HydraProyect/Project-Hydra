namespace CaeManager.E2ETests;

/// <summary>
/// Conexión a PostgreSQL para <see cref="WebAppFixture"/> — mismo criterio que
/// <c>CaeManager.IntegrationTests.BaseDatosPostgresDePruebas</c> (proyectos de
/// test distintos, sin referencia cruzada entre ellos, así que se duplica esta
/// clase pequeña en vez de compartirla). Por defecto apunta al servidor local
/// (sin Docker en la máquina de desarrollo); en CI se apunta al servicio de
/// postgres del workflow con <c>CAEMANAGER_TESTS_PG</c>.
/// </summary>
internal static class BaseDatosPostgresDePruebas
{
    private static readonly string Servidor =
        Environment.GetEnvironmentVariable("CAEMANAGER_TESTS_PG")
        ?? "Host=localhost;Username=postgres;Password=postgres";

    internal static string CadenaConexionUnica(string prefijo) =>
        $"{Servidor};Database=caemanager_{prefijo}_{Guid.NewGuid():N}";

    internal static async Task EliminarAsync(string cadenaConexion)
    {
        var constructor = new Npgsql.NpgsqlConnectionStringBuilder(cadenaConexion);
        var baseDatos = constructor.Database;
        constructor.Database = "postgres";

        await using var conexion = new Npgsql.NpgsqlConnection(constructor.ConnectionString);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"DROP DATABASE IF EXISTS \"{baseDatos}\" WITH (FORCE);";
        await comando.ExecuteNonQueryAsync();
    }
}
