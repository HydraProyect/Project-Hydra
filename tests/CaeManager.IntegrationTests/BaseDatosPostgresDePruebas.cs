using Npgsql;
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

    /// <summary>
    /// Tope de conexiones POR CLASE de test — la causa de la inestabilidad que
    /// la suite arrastraba (diagnosticada 2026-08-13).
    ///
    /// Cada clase usa una cadena distinta (base propia), y Npgsql abre un pool
    /// por cadena con <c>Maximum Pool Size = 100</c> por defecto. Con 74 clases
    /// y xUnit paralelizando por núcleo (16 en la máquina de desarrollo), la
    /// suma de conexiones vivas superaba el <c>max_connections = 100</c> del
    /// servidor: la clase que tocaba arrancar en ese momento moría en el
    /// handshake de autenticación ("se ha forzado la interrupción de una
    /// conexión existente por el host remoto"), siempre dentro de
    /// <c>InitializeAsync</c> y siempre en una clase distinta — por eso parecía
    /// aleatorio y por eso pasaba en aislamiento.
    ///
    /// El total de conexiones vivas es <c>clases en paralelo × conexiones por
    /// clase</c>, así que se acotan los dos factores: el paralelismo de xUnit
    /// baja a 6 en <c>xunit.runner.json</c> (por defecto usaba los 16 núcleos)
    /// y aquí se limita el pool de cada clase. 6 × 10 = 60 en el peor caso,
    /// con holgura bajo el límite de 100.
    ///
    /// El tope por clase NO puede ser mínimo: varios tests anidan contextos
    /// (<c>await using</c> dentro de otro <c>await using</c>) y algunos abren
    /// el suyo mientras el del <c>InitializeAsync</c> sigue vivo — con 4 la
    /// suite entera moría por "connection pool has been exhausted", medido.
    ///
    /// La poda devuelve al servidor los slots de una clase ya terminada, en
    /// vez de retenerlos hasta que muera el proceso de test.
    /// </summary>
    /// <remarks>
    /// <c>Connection Idle Lifetime</c> tiene que ser mayor que
    /// <c>Connection Pruning Interval</c> o Npgsql rechaza la cadena entera al
    /// construir el pool.
    /// </remarks>
    private const string LimitesDePool =
        "Maximum Pool Size=10;Minimum Pool Size=0;Connection Idle Lifetime=15;Connection Pruning Interval=5";

    /// <summary>
    /// Para el bootstrap de clúster: base de mantenimiento y sin pool. Corre una
    /// sola vez por proceso y no debe dejar conexiones vivas compitiendo con las
    /// de la suite, que ya vive al límite de <c>max_connections</c>.
    /// </summary>
    internal static string CadenaDeMantenimientoSinPool() =>
        $"{Servidor};Database=postgres;Pooling=false";

    internal static string CadenaConexionUnica() =>
        $"{Servidor};Database=caemanager_tests_{Guid.NewGuid():N};{LimitesDePool}";

    /// <summary>
    /// Contraseña de <c>cae_app_runtime</c> en el clúster de pruebas. Fija y en
    /// claro a propósito: no protege nada —el clúster de tests ya usa
    /// <c>postgres/postgres</c>— y su único fin es permitir una conexión de LOGIN
    /// real con ese rol.
    /// </summary>
    internal const string ContrasenaRuntimeDePruebas = "runtime-de-pruebas";

    /// <summary>
    /// La misma base, pero <b>autenticando como <c>cae_app_runtime</c></b> en vez
    /// de adoptarlo con <c>SET ROLE</c> desde el propietario.
    ///
    /// <para>
    /// La diferencia importa: <c>SET ROLE</c> demuestra que las políticas se
    /// aplican, pero parte de una sesión que ya entró como superusuario. Una
    /// conexión de login reproduce además la autenticación y los privilegios
    /// efectivos del rol, que es lo que hace producción.
    /// </para>
    ///
    /// <para>
    /// <b>Esta vía solo existe desde #256.</b> Antes, el bootstrap de clúster
    /// convergía ese rol a <c>NOLOGIN</c> en cada ejecución, así que el arnés le
    /// habría retirado el LOGIN justo después de concedérselo.
    /// </para>
    /// </summary>
    internal static string CadenaComoRuntime(string cadenaDeLaBase)
    {
        var constructor = new NpgsqlConnectionStringBuilder(cadenaDeLaBase)
        {
            Username = "cae_app_runtime",
            Password = ContrasenaRuntimeDePruebas,
        };

        return constructor.ConnectionString;
    }

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

        // Devolver los slots del pool de ESTA clase al servidor antes de nada.
        // Sin esto, las conexiones ociosas del pool seguían contando contra
        // max_connections hasta el final del proceso de test, aunque su base ya
        // no existiera (se encontraron 92 bases huérfanas acumuladas).
        Npgsql.NpgsqlConnection.ClearPool(new Npgsql.NpgsqlConnection(cadenaConexion));

        constructor.Database = "postgres";

        await using var conexion = new Npgsql.NpgsqlConnection(constructor.ConnectionString);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"DROP DATABASE IF EXISTS \"{baseDatos}\" WITH (FORCE);";
        await comando.ExecuteNonQueryAsync();
    }

    // Nota: las bases huérfanas de ejecuciones interrumpidas (Ctrl+C, proceso
    // muerto) no se limpian solas a propósito — un barrido automático con
    // DROP ... WITH (FORCE) mataría las conexiones de una segunda ejecución
    // simultánea en la misma máquina. Se limpian a mano cuando molesten:
    //   SELECT datname FROM pg_database WHERE datname LIKE 'caemanager_tests_%';
}
