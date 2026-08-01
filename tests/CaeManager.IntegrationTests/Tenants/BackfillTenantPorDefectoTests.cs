using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Valida el backfill real de la Etapa 2 (PLAN-MIGRACION-MULTITENANT.md § 3):
/// parte de una base de datos migrada solo hasta <c>AgregarTenantNullable</c>
/// (esquema con TenantId nullable, como quedaría el tenant #1 real justo
/// antes del backfill), inserta filas sintéticas con TenantId NULL —
/// simulando datos de producción preexistentes, no una base vacía — y
/// verifica que <c>SembrarTenantPorDefecto</c> las sella todas al tenant por
/// defecto. Esta es la prueba que exige la regla de "cada cambio incorpora
/// sus pruebas antes de continuar": sin ella, una base vacía de test no
/// demuestra nada sobre el caso real que importa.
///
/// Es el único test de integración que sigue sobre SQLite tras llevar la
/// suite a PostgreSQL: reproduce un punto intermedio del historial de
/// migraciones de SQLite (<c>AgregarTenantNullable</c>) que en el ensamblado
/// de PostgreSQL no existe — su línea base nace ya con TenantId NOT NULL, así
/// que el backfill nunca correrá sobre ese motor. Se retira junto con la rama
/// SQLite cuando se complete el corte (ver ProveedorBaseDatos).
/// </summary>
public class BackfillTenantPorDefectoTests : IAsyncLifetime
{
    private const string MigracionEsquemaAditivo = "20260723215719_AgregarTenantNullable";

    private readonly string _rutaBaseDatos = Path.Combine(Path.GetTempPath(), $"caemanager-backfill-{Guid.NewGuid()}.db");
    private CaeManagerDbContext _dbContext = null!;
    private Guid _idClienteSintetico;
    private Guid _idAlertaSintetica;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseSqlite($"Data Source={_rutaBaseDatos}")
            .Options;

        // Este test opera vía SQL crudo/migraciones, no a través del DbSet
        // filtrado — el tenant ambiental no es relevante aquí, pero el
        // constructor lo exige.
        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), new TenantActualAmbiental());

        // 1. Migrar solo hasta el esquema aditivo (Etapa 1) — TenantId ya
        // existe como columna nullable, pero el backfill (Etapa 2) todavía
        // no se ha aplicado.
        var migrator = _dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(MigracionEsquemaAditivo);

        // 2. Insertar filas sintéticas con TenantId NULL — representan datos
        // reales que ya existían en producción antes de la Etapa 1.
        await InsertarDatosPreexistentesAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        // Microsoft.Data.Sqlite devuelve la conexión a su pool al disponer el
        // contexto, y ese handle sigue abierto: en Windows impide borrar el
        // archivo y hacía fallar el teardown (no el cuerpo) del test.
        SqliteConnection.ClearAllPools();
        if (File.Exists(_rutaBaseDatos)) File.Delete(_rutaBaseDatos);
    }

    private async Task InsertarDatosPreexistentesAsync()
    {
        _idClienteSintetico = Guid.NewGuid();
        _idAlertaSintetica = Guid.NewGuid();

        var conexion = (SqliteConnection)_dbContext.Database.GetDbConnection();
        await _dbContext.Database.OpenConnectionAsync();

        await using (var comandoCliente = conexion.CreateCommand())
        {
            comandoCliente.CommandText = """
                INSERT INTO "Clientes"
                    ("Id", "RazonSocial", "Cif", "EsCritico", "Notas", "EjecutivoUsuarioId",
                     "CreadoEnUtc", "EstaEliminado", "EliminadoEnUtc", "EliminadoPorUsuarioId", "TenantId")
                VALUES
                    ($id, 'RENDELSUR (dato preexistente)', 'B12345674', 0, NULL, NULL,
                     '2026-01-01 00:00:00', 0, NULL, NULL, NULL);
                """;
            comandoCliente.Parameters.AddWithValue("$id", _idClienteSintetico.ToString());
            await comandoCliente.ExecuteNonQueryAsync();
        }

        await using (var comandoAlerta = conexion.CreateCommand())
        {
            comandoAlerta.CommandText = """
                INSERT INTO "Alertas" ("Id", "DocumentoId", "Nivel", "FechaGeneracionUtc", "TenantId")
                VALUES ($id, $documentoId, 'Urgente', '2026-01-01 00:00:00', NULL);
                """;
            comandoAlerta.Parameters.AddWithValue("$id", _idAlertaSintetica.ToString());
            comandoAlerta.Parameters.AddWithValue("$documentoId", Guid.NewGuid().ToString());
            await comandoAlerta.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task El_backfill_sella_al_tenant_por_defecto_las_filas_que_ya_existian_antes_de_la_migracion()
    {
        // Antes del backfill: confirmamos que de verdad partimos de TenantId NULL
        // (si esto fallara, el resto del test no probaría nada).
        var clienteAntes = await LeerTenantIdAsync("Clientes", _idClienteSintetico);
        var alertaAntes = await LeerTenantIdAsync("Alertas", _idAlertaSintetica);
        clienteAntes.Should().BeNull();
        alertaAntes.Should().BeNull();

        // 3. Aplicar el resto de migraciones — incluida SembrarTenantPorDefecto.
        var migrator = _dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync();

        // 4. Verificar que el backfill selló ambas filas preexistentes.
        var clienteDespues = await LeerTenantIdAsync("Clientes", _idClienteSintetico);
        var alertaDespues = await LeerTenantIdAsync("Alertas", _idAlertaSintetica);

        clienteDespues.Should().Be(TenantSeedData.IdPorDefecto);
        alertaDespues.Should().Be(TenantSeedData.IdPorDefecto);
    }

    private async Task<Guid?> LeerTenantIdAsync(string tabla, Guid id)
    {
        var conexion = (SqliteConnection)_dbContext.Database.GetDbConnection();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"""SELECT "TenantId" FROM "{tabla}" WHERE "Id" = $id;""";
        comando.Parameters.AddWithValue("$id", id.ToString());

        var resultado = await comando.ExecuteScalarAsync();
        return resultado is null or DBNull ? null : Guid.Parse((string)resultado);
    }
}
