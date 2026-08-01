namespace CaeManager.IntegrationTests;

/// <summary>
/// Conexión a PostgreSQL para los tests de integración. Cada test crea su
/// propia base de datos con nombre único (las fixtures corren en paralelo) y
/// la borra en el teardown con <c>EnsureDeletedAsync</c> — Npgsql vacía su
/// pool de conexiones antes del DROP, así que no hace falta el equivalente
/// del <c>ClearAllPools</c> que exigía SQLite.
///
/// Por defecto apunta al servidor local (sin Docker en la máquina de
/// desarrollo, ver ROADMAP.md § migración a PostgreSQL); en CI se apunta al
/// servicio de postgres del workflow con la variable
/// <c>CAEMANAGER_TESTS_PG</c> (cadena sin <c>Database=</c>, que se añade
/// aquí).
/// </summary>
internal static class BaseDatosPostgresDePruebas
{
    private static readonly string Servidor =
        Environment.GetEnvironmentVariable("CAEMANAGER_TESTS_PG")
        ?? "Host=localhost;Username=postgres;Password=postgres";

    internal static string CadenaConexionUnica() =>
        $"{Servidor};Database=caemanager_tests_{Guid.NewGuid():N}";

    /// <summary>
    /// Borra la base de datos del test sin pasar por un DbContext — los tests
    /// que crean el contexto por método (<c>await using var contexto = ...</c>)
    /// no tienen ninguno vivo en el teardown. <c>WITH (FORCE)</c> corta las
    /// conexiones del pool que pudieran quedar, el papel que en SQLite hacía
    /// <c>ClearAllPools</c>.
    /// </summary>
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
