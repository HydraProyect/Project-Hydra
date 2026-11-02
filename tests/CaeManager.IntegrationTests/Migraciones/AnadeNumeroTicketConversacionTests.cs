using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// Relleno histórico del número de ticket: se deshace la migración, se siembran conversaciones como las que ya hay
/// en staging y producción, y se vuelve a aplicar. Sin contador rellenado, la primera alta de un Tenant con historial
/// recibiría el 1 y chocaría con el índice único.
/// </summary>
public class AnadeNumeroTicketConversacionTests : IAsyncLifetime
{
    private const string Migracion = "AnadeNumeroTicketConversacion";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly DateTime _inicio = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    public Task InitializeAsync() => BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Rellena_por_Tenant_y_fecha_incluidas_las_eliminadas_y_deja_el_contador_al_dia()
    {
        await using (var contexto = ContextoParaMigrar())
        {
            var migrador = contexto.GetService<IMigrator>();
            var migraciones = contexto.Database.GetMigrations().ToList();
            var indice = migraciones.FindIndex(m => m.EndsWith($"_{Migracion}", StringComparison.Ordinal));
            indice.Should().BeGreaterThan(0);
            await migrador.MigrateAsync(migraciones[indice - 1]);
        }

        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        var terceraDeA = await SembrarAsync(conexion, _tenantA, _inicio.AddHours(2), eliminada: false);
        var primeraDeA = await SembrarAsync(conexion, _tenantA, _inicio, eliminada: false);
        var eliminadaDeA = await SembrarAsync(conexion, _tenantA, _inicio.AddHours(1), eliminada: true);
        var primeraDeB = await SembrarAsync(conexion, _tenantB, _inicio.AddHours(3), eliminada: false);

        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        (await NumeroDeAsync(conexion, primeraDeA)).Should().Be(1);
        (await NumeroDeAsync(conexion, eliminadaDeA)).Should().Be(2, "una eliminada conserva su número: puede restaurarse");
        (await NumeroDeAsync(conexion, terceraDeA)).Should().Be(3);
        (await NumeroDeAsync(conexion, primeraDeB)).Should().Be(1, "el correlativo es por Tenant propietario");

        await using var ejecucion = new NpgsqlConnection(_cadenaConexion);
        await ejecucion.OpenAsync();
        await using (var setRol = ejecucion.CreateCommand())
        {
            setRol.CommandText = "SET ROLE cae_app_runtime;";
            await setRol.ExecuteNonQueryAsync();
        }
        await ContextoRlsDePruebas.FijarAsync(ejecucion, tenantId: _tenantA);

        var nueva = await SembrarAsync(ejecucion, _tenantA, _inicio.AddHours(4), eliminada: false);

        (await NumeroDeAsync(ejecucion, nueva)).Should().Be(4, "el contador arranca en el máximo rellenado");
    }

    private static async Task<long> NumeroDeAsync(NpgsqlConnection conexion, Guid conversacionId)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """SELECT "NumeroTicket" FROM "Conversaciones" WHERE "Id" = @id;""";
        comando.Parameters.AddWithValue("id", conversacionId);
        return (long)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task<Guid> SembrarAsync(NpgsqlConnection conexion, Guid tenant, DateTime creadoEnUtc, bool eliminada)
    {
        var id = Guid.NewGuid();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            INSERT INTO "Conversaciones" ("Id", "TenantId", "Asunto", "Estado", "EstadoDesdeUtc", "FechaUltimoMensajeUtc",
                                          "Canal", "Prioridad", "CreadoEnUtc", "EstaEliminado", "Version")
            VALUES (@id, @tenant, 'Hilo', 0, @creado, @creado, 0, 0, @creado, @eliminada, @version);
            """;
        comando.Parameters.AddWithValue("id", id);
        comando.Parameters.AddWithValue("tenant", tenant);
        comando.Parameters.AddWithValue("creado", creadoEnUtc);
        comando.Parameters.AddWithValue("eliminada", eliminada);
        comando.Parameters.AddWithValue("version", Guid.NewGuid());
        await comando.ExecuteNonQueryAsync();
        return id;
    }

    private CaeManagerDbContext ContextoParaMigrar()
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), new TenantActualAmbiental());
    }
}
