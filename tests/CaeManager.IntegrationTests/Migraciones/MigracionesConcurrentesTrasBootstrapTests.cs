using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// La prueba de que la arquitectura nueva sacó la carrera del camino de
/// migración, no de que la carrera existía —eso ya está demostrado—.
///
/// <para>
/// El suceso reconstruido por la traza instrumentada fue: seis migradores
/// entran en <c>RolSoporteSoloLectura</c> en 9 ms y, 125 ms después, tres
/// fallan con <c>42704</c> dentro de su propio bloque y tres pasan. Aquí se
/// reproduce exactamente esa forma —seis migradores concurrentes, seis bases
/// nuevas del mismo clúster— con la diferencia que importa: los principales ya
/// existen porque el bootstrap los proveyó una sola vez, antes de que arrancara
/// ninguno.
/// </para>
///
/// <para>
/// <b>Lo que este test NO hace, y es deliberado:</b> no borra los roles para
/// comprobar que las migraciones fallan sin ellos. Un rol es un objeto de
/// clúster: borrarlo desde un test tumbaría a las otras 88 clases que corren en
/// paralelo contra el mismo servidor. Esa mitad se verifica fuera de la suite
/// —quitando el rol y comprobando que el arnés arranca igual porque el
/// inicializador lo repone— y la mitad estructural la cubre el ratchet de
/// arquitectura, que prohíbe crear roles bajo <c>Migrations/</c>.
/// </para>
/// </summary>
public class MigracionesConcurrentesTrasBootstrapTests : IAsyncLifetime
{
    private const int Migradores = 6;   // = maxParallelThreads del arnés

    private readonly string[] _bases =
        [.. Enumerable.Range(0, Migradores).Select(_ => $"caemanager_conc_{Guid.NewGuid():N}")];

    public async Task InitializeAsync()
    {
        foreach (var bd in _bases)
        {
            await using var conexion = new NpgsqlConnection(
                BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool());
            await conexion.OpenAsync();
            await using var comando = new NpgsqlCommand($"CREATE DATABASE {bd}", conexion);
            await comando.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var bd in _bases)
        {
            await using var conexion = new NpgsqlConnection(
                BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool());
            await conexion.OpenAsync();
            await using var comando = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS {bd} WITH (FORCE)", conexion);
            await comando.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Los_principales_del_cluster_existen_antes_de_migrar_nada()
    {
        await using var conexion = new NpgsqlConnection(
            BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool());
        await conexion.OpenAsync();

        await using var comando = new NpgsqlCommand(
            """
            SELECT rolname FROM pg_roles
            WHERE rolname IN ('cae_app_runtime', 'cae_app_soporte')
              AND NOT rolcanlogin AND NOT rolsuper AND NOT rolcreatedb
              AND NOT rolcreaterole AND NOT rolbypassrls
            ORDER BY rolname
            """, conexion);

        var encontrados = new List<string>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync()) encontrados.Add(lector.GetString(0));

        encontrados.Should().BeEquivalentTo(["cae_app_runtime", "cae_app_soporte"],
            "el bootstrap corre una vez por proceso antes de cualquier fixture; si esto falla, " +
            "las migraciones de toda la suite están operando sobre un clúster fuera de contrato");
    }

    [Fact]
    public async Task Seis_migradores_simultaneos_completan_sin_disputarse_ningun_rol()
    {
        var barrera = new Barrier(Migradores);

        var resultados = await Task.WhenAll(_bases.Select(bd => Task.Run(async () =>
        {
            var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
                .UseNpgsql($"{BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool()}"
                        .Replace("Database=postgres", $"Database={bd}"),
                    npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
                .Options;

            await using var contexto = new CaeManagerDbContext(
                opciones, new EphemeralDataProtectionProvider(), new TenantActualAmbiental());

            // Arrancan a la vez: es la condición que producía el fallo.
            barrera.SignalAndWait();

            try
            {
                await contexto.Database.MigrateAsync();
                return (Base: bd, Error: (string?)null);
            }
            catch (PostgresException ex)
            {
                return (Base: bd, Error: $"{ex.SqlState}: {ex.MessageText}");
            }
        })));

        resultados.Where(r => r.Error is not null).Should().BeEmpty(
            "los principales del clúster ya existían, así que ninguna migración tiene nada que " +
            "crear ni que disputar; un 42704 aquí significaría que alguna volvió a hacerse " +
            "responsable de un objeto que no es suyo");
    }
}
