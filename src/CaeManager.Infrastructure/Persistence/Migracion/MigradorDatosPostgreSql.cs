using System.Globalization;
using CaeManager.Infrastructure.MultiTenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace CaeManager.Infrastructure.Persistence.Migracion;

/// <summary>
/// Copia de un solo uso de todos los datos del SQLite de producción al
/// PostgreSQL de destino, para el corte de ADR-003 (punto 7 del backlog de
/// migración en ROADMAP.md). Se invoca desde Program.cs cuando
/// <c>MigracionDatosPostgreSql:Destino</c> está configurado, y el proceso
/// termina sin arrancar el servidor.
///
/// Decisiones que importan:
/// - <b>ADO crudo, no EF</b>: se copia la representación almacenada tal cual
///   — sin filtros globales (las filas soft-deleted y las de todos los
///   tenants viajan también), y sin pasar por los value converters de
///   cifrado: el texto cifrado de las credenciales se copia intacto, que es
///   exactamente lo correcto porque las claves de Data Protection viajan
///   junto a la base de datos en el corte (RUNBOOK-CLAVES.md).
/// - <b>El destino se migra primero y se vacía después</b>: MigrateAsync
///   crea el esquema con la línea base (que incluye los HasData — tenant por
///   defecto, catálogo de tipos, roles); esas filas también existen en el
///   origen (posiblemente modificadas), así que se vacían todas las tablas
///   antes de copiar para que el origen sea la única fuente de verdad.
/// - <b>FKs en DEFERRABLE durante una única transacción</b>: evita tener que
///   ordenar topológicamente 50+ tablas (con auto-referencias como
///   AspNetUsers.CoordinadorUsuarioId); todo se valida junto en el COMMIT, y
///   al final se devuelven a NOT DEFERRABLE para que el esquema quede igual
///   que un despliegue limpio.
/// - <b>Se verifica antes de dar nada por bueno</b>: congruencia de columnas
///   tabla a tabla entre ambos esquemas (protege contra deriva entre los dos
///   juegos de migraciones) y recuento de filas origen vs. destino.
/// </summary>
public static class MigradorDatosPostgreSql
{
    public static async Task EjecutarAsync(
        string cadenaOrigenSqlite, string cadenaDestinoPostgreSql, bool sobrescribirDestino, ILogger logger)
    {
        logger.LogInformation("Migración de datos SQLite → PostgreSQL: empezando.");

        // 1. Esquema del destino al día (crea la base de datos si no existe).
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(cadenaDestinoPostgreSql, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;
        await using (var contextoDestino = new CaeManagerDbContext(
            opciones, new EphemeralDataProtectionProvider(), new TenantActualAmbiental()))
        {
            await contextoDestino.Database.MigrateAsync();
        }

        await using var destino = new NpgsqlConnection(cadenaDestinoPostgreSql);
        await destino.OpenAsync();

        await using var origen = new SqliteConnection(new SqliteConnectionStringBuilder(cadenaOrigenSqlite)
        {
            Mode = SqliteOpenMode.ReadOnly
        }.ConnectionString);
        await origen.OpenAsync();

        // 2. Guardarraíl contra re-ejecuciones: si el destino ya tiene datos
        // reales (no solo la semilla de la línea base), esto pisaría una base
        // viva con el estado del SQLite — probablemente más viejo.
        if (!sobrescribirDestino && await TieneDatosRealesAsync(destino))
        {
            throw new InvalidOperationException(
                "El destino ya contiene datos reales (Clientes o AspNetUsers). Si de verdad quieres " +
                "sobrescribirlos con el contenido del SQLite de origen, configura " +
                "MigracionDatosPostgreSql:SobrescribirDestino=true.");
        }

        var tablas = await ListarTablasDestinoAsync(destino);
        var columnasPorTabla = await LeerColumnasDestinoAsync(destino);
        VerificarCongruenciaDeEsquemas(origen, tablas, columnasPorTabla);

        // 3. FKs a DEFERRABLE — se restauran siempre, también si la copia falla.
        var restricciones = await ListarForeignKeysAsync(destino);
        await CambiarDeferrableAsync(destino, restricciones, deferrable: true);

        try
        {
            await using (var transaccion = await destino.BeginTransactionAsync())
            {
                await EjecutarAsync(destino, "SET CONSTRAINTS ALL DEFERRED;");

                var listaTruncate = string.Join(", ", tablas.Select(t => $"\"{t}\""));
                await EjecutarAsync(destino, $"TRUNCATE TABLE {listaTruncate} RESTART IDENTITY CASCADE;");

                foreach (var tabla in tablas)
                {
                    var filas = await CopiarTablaAsync(origen, destino, tabla, columnasPorTabla[tabla]);
                    logger.LogInformation("  {Tabla}: {Filas} filas copiadas.", tabla, filas);
                }

                await transaccion.CommitAsync();
            }

            await ReiniciarSecuenciasIdentityAsync(destino, columnasPorTabla);
        }
        finally
        {
            await CambiarDeferrableAsync(destino, restricciones, deferrable: false);
        }

        // 4. Verificación de recuentos, tabla a tabla — fuera de la
        // transacción, contra lo realmente confirmado.
        foreach (var tabla in tablas)
        {
            var enOrigen = await ContarAsync(origen, tabla);
            var enDestino = await ContarAsync(destino, tabla);
            if (enOrigen != enDestino)
                throw new InvalidOperationException(
                    $"Verificación fallida: \"{tabla}\" tiene {enOrigen} filas en origen y {enDestino} en destino.");
        }

        logger.LogInformation(
            "Migración de datos completada y verificada: {Tablas} tablas con recuentos idénticos en ambos motores.",
            tablas.Count);
    }

    private static async Task<bool> TieneDatosRealesAsync(NpgsqlConnection destino)
    {
        await using var comando = destino.CreateCommand();
        comando.CommandText = """SELECT (SELECT count(*) FROM "Clientes") + (SELECT count(*) FROM "AspNetUsers");""";
        return Convert.ToInt64(await comando.ExecuteScalarAsync(), CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<List<string>> ListarTablasDestinoAsync(NpgsqlConnection destino)
    {
        var tablas = new List<string>();
        await using var comando = destino.CreateCommand();
        comando.CommandText =
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory' ORDER BY tablename;";
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            tablas.Add(lector.GetString(0));
        return tablas;
    }

    private sealed record Columna(string Nombre, string TipoUdt, bool EsIdentity);

    private static async Task<Dictionary<string, List<Columna>>> LeerColumnasDestinoAsync(NpgsqlConnection destino)
    {
        var resultado = new Dictionary<string, List<Columna>>();
        await using var comando = destino.CreateCommand();
        comando.CommandText =
            "SELECT table_name, column_name, udt_name, is_identity FROM information_schema.columns " +
            "WHERE table_schema = 'public' ORDER BY table_name, ordinal_position;";
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            var tabla = lector.GetString(0);
            if (!resultado.TryGetValue(tabla, out var columnas))
                resultado[tabla] = columnas = [];
            columnas.Add(new Columna(lector.GetString(1), lector.GetString(2), lector.GetString(3) == "YES"));
        }

        return resultado;
    }

    /// <summary>
    /// Ambos esquemas nacen del mismo modelo de EF pero por juegos de
    /// migraciones distintos — si divergen (una columna que existe en uno y
    /// no en el otro), mejor un error con nombres concretos que un COPY que
    /// falle a mitad o, peor, complete callándose una columna.
    /// </summary>
    private static void VerificarCongruenciaDeEsquemas(
        SqliteConnection origen, List<string> tablas, Dictionary<string, List<Columna>> columnasPorTabla)
    {
        foreach (var tabla in tablas)
        {
            using var comando = origen.CreateCommand();
            comando.CommandText = $"PRAGMA table_info(\"{tabla}\");";
            using var lector = comando.ExecuteReader();

            var columnasOrigen = new List<string>();
            while (lector.Read())
                columnasOrigen.Add(lector.GetString(1));

            if (columnasOrigen.Count == 0)
                throw new InvalidOperationException($"La tabla \"{tabla}\" no existe en el SQLite de origen.");

            var columnasDestino = columnasPorTabla[tabla].Select(c => c.Nombre).ToList();
            var soloOrigen = columnasOrigen.Except(columnasDestino).ToList();
            var soloDestino = columnasDestino.Except(columnasOrigen).ToList();
            if (soloOrigen.Count > 0 || soloDestino.Count > 0)
                throw new InvalidOperationException(
                    $"Los esquemas divergen en \"{tabla}\": solo en origen [{string.Join(", ", soloOrigen)}], " +
                    $"solo en destino [{string.Join(", ", soloDestino)}].");
        }
    }

    private sealed record ForeignKey(string Tabla, string Nombre);

    private static async Task<List<ForeignKey>> ListarForeignKeysAsync(NpgsqlConnection destino)
    {
        var claves = new List<ForeignKey>();
        await using var comando = destino.CreateCommand();
        comando.CommandText =
            "SELECT conrelid::regclass::text, conname FROM pg_constraint " +
            "WHERE contype = 'f' AND connamespace = 'public'::regnamespace;";
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            claves.Add(new ForeignKey(lector.GetString(0), lector.GetString(1)));
        return claves;
    }

    private static async Task CambiarDeferrableAsync(
        NpgsqlConnection destino, List<ForeignKey> claves, bool deferrable)
    {
        foreach (var clave in claves)
        {
            var modo = deferrable ? "DEFERRABLE" : "NOT DEFERRABLE";
            await EjecutarAsync(destino, $"ALTER TABLE {clave.Tabla} ALTER CONSTRAINT \"{clave.Nombre}\" {modo};");
        }
    }

    private static async Task<long> CopiarTablaAsync(
        SqliteConnection origen, NpgsqlConnection destino, string tabla, List<Columna> columnas)
    {
        var listaColumnas = string.Join(", ", columnas.Select(c => $"\"{c.Nombre}\""));

        await using var comandoOrigen = origen.CreateCommand();
        comandoOrigen.CommandText = $"SELECT {listaColumnas} FROM \"{tabla}\";";
        await using var lector = await comandoOrigen.ExecuteReaderAsync();

        var filas = 0L;
        await using var importador = await destino.BeginBinaryImportAsync(
            $"COPY \"{tabla}\" ({listaColumnas}) FROM STDIN (FORMAT BINARY)");

        while (await lector.ReadAsync())
        {
            await importador.StartRowAsync();
            for (var i = 0; i < columnas.Count; i++)
                await EscribirValorAsync(importador, lector.GetValue(i), columnas[i], tabla);
            filas++;
        }

        await importador.CompleteAsync();
        return filas;
    }

    /// <summary>
    /// Convierte la representación almacenada por EF sobre SQLite (que solo
    /// tiene INTEGER/REAL/TEXT/BLOB) al tipo real de la columna en
    /// PostgreSQL. Cualquier tipo no contemplado falla con nombres concretos
    /// en vez de dejar pasar una conversión dudosa.
    /// </summary>
    private static async Task EscribirValorAsync(
        NpgsqlBinaryImporter importador, object valor, Columna columna, string tabla)
    {
        if (valor is DBNull)
        {
            await importador.WriteNullAsync();
            return;
        }

        switch (columna.TipoUdt)
        {
            case "uuid":
                await importador.WriteAsync(Guid.Parse((string)valor), NpgsqlDbType.Uuid);
                break;
            case "timestamptz":
                // EF sobre SQLite guarda DateTime como texto sin zona (los
                // valores son UTC por convención *Utc del dominio) y
                // DateTimeOffset (LockoutEnd) como texto con desplazamiento —
                // AssumeUniversal cubre lo primero y AdjustToUniversal
                // normaliza lo segundo.
                var momento = DateTime.Parse((string)valor, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                await importador.WriteAsync(momento, NpgsqlDbType.TimestampTz);
                break;
            case "date":
                await importador.WriteAsync(DateOnly.Parse((string)valor, CultureInfo.InvariantCulture), NpgsqlDbType.Date);
                break;
            case "bool":
                await importador.WriteAsync(Convert.ToInt64(valor, CultureInfo.InvariantCulture) != 0, NpgsqlDbType.Boolean);
                break;
            case "numeric":
                var numero = valor is string texto
                    ? decimal.Parse(texto, CultureInfo.InvariantCulture)
                    : Convert.ToDecimal(valor, CultureInfo.InvariantCulture);
                await importador.WriteAsync(numero, NpgsqlDbType.Numeric);
                break;
            case "int4":
                await importador.WriteAsync(Convert.ToInt32(valor, CultureInfo.InvariantCulture), NpgsqlDbType.Integer);
                break;
            case "int8":
                await importador.WriteAsync(Convert.ToInt64(valor, CultureInfo.InvariantCulture), NpgsqlDbType.Bigint);
                break;
            case "float8":
                await importador.WriteAsync(Convert.ToDouble(valor, CultureInfo.InvariantCulture), NpgsqlDbType.Double);
                break;
            case "text":
                await importador.WriteAsync((string)valor, NpgsqlDbType.Text);
                break;
            case "varchar":
                await importador.WriteAsync((string)valor, NpgsqlDbType.Varchar);
                break;
            case "bytea":
                await importador.WriteAsync((byte[])valor, NpgsqlDbType.Bytea);
                break;
            default:
                throw new InvalidOperationException(
                    $"Tipo de columna sin conversión definida: {columna.TipoUdt} ({tabla}.{columna.Nombre}). " +
                    "Añade el caso a MigradorDatosPostgreSql antes de migrar.");
        }
    }

    /// <summary>
    /// Las columnas identity (p. ej. AspNetUserClaims.Id, int autonumérico)
    /// se copian con sus valores originales; sin esto, la secuencia seguiría
    /// en 1 y el primer insert de la app chocaría con un Id ya copiado.
    /// </summary>
    private static async Task ReiniciarSecuenciasIdentityAsync(
        NpgsqlConnection destino, Dictionary<string, List<Columna>> columnasPorTabla)
    {
        foreach (var (tabla, columnas) in columnasPorTabla)
        {
            foreach (var columna in columnas.Where(c => c.EsIdentity))
            {
                await EjecutarAsync(destino,
                    $"SELECT setval(pg_get_serial_sequence('\"{tabla}\"', '{columna.Nombre}'), " +
                    $"COALESCE((SELECT MAX(\"{columna.Nombre}\") FROM \"{tabla}\"), 0) + 1, false);");
            }
        }
    }

    private static async Task<long> ContarAsync(System.Data.Common.DbConnection conexion, string tabla)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"SELECT count(*) FROM \"{tabla}\";";
        return Convert.ToInt64(await comando.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task EjecutarAsync(NpgsqlConnection conexion, string sql)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        await comando.ExecuteNonQueryAsync();
    }
}
