using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// La migración <c>CapacidadEnSesionPrivilegiada</c> sobre una base que ya
/// tenía Sesiones Privilegiadas: cada una toma la capacidad de su concesión, y
/// la tabla sigue con FORCE ROW LEVEL SECURITY al terminar (el relleno lo retira
/// solo dentro de la transacción).
/// </summary>
public class MigracionCapacidadEnSesionPrivilegiadaTests : IAsyncLifetime
{
    private const string MigracionAnterior = "20260925164459_AccesosSoporteTalvegVisiblesParaAdministrador";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Las_sesiones_existentes_toman_la_capacidad_de_su_concesion_y_la_RLS_sigue_forzada()
    {
        await MigrarAsync(MigracionAnterior);

        var concesion = Guid.NewGuid();
        var sesion = Guid.NewGuid();
        var tecnico = Guid.NewGuid();
        var tenant = Guid.NewGuid();

        // Siembra por SQL: el modelo de EF ya tiene la columna nueva y el esquema
        // intermedio no. Como propietario, con FORCE retirado solo para sembrar
        // (base de prueba propia): las políticas exigen app.usuario_id.
        await using (var conexion = new NpgsqlConnection(_cadenaConexion))
        {
            await conexion.OpenAsync();
            await using var sembrar = new NpgsqlCommand(
                """
                ALTER TABLE "ConcesionesPrivilegio" NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE "SesionesPrivilegiadas" NO FORCE ROW LEVEL SECURITY;
                INSERT INTO "ConcesionesPrivilegio"
                    ("Id", "Capacidad", "CreadoEnUtc", "EsAlcanceGlobal", "Estado", "Origen",
                     "UsuarioPlataformaId", "Version", "VigenciaDesde", "VigenciaHasta")
                VALUES (@concesion, 'Aprovisionamiento', now(), false, 'Vigente', 'Ordinaria',
                        @tecnico, gen_random_uuid(), now() - interval '1 day', now() + interval '1 day');
                INSERT INTO "SesionesPrivilegiadas"
                    ("Id", "ConcesionPrivilegioId", "TenantObjetivoId", "Motivo",
                     "InicioEnUtc", "ExpiraEnUtc", "Version")
                VALUES (@sesion, @concesion, @tenant, 'Importación inicial',
                        now(), now() + interval '1 hour', gen_random_uuid());
                ALTER TABLE "ConcesionesPrivilegio" FORCE ROW LEVEL SECURITY;
                ALTER TABLE "SesionesPrivilegiadas" FORCE ROW LEVEL SECURITY;
                """, conexion);
            sembrar.Parameters.AddWithValue("concesion", concesion);
            sembrar.Parameters.AddWithValue("sesion", sesion);
            sembrar.Parameters.AddWithValue("tecnico", tecnico);
            sembrar.Parameters.AddWithValue("tenant", tenant);
            await sembrar.ExecuteNonQueryAsync();
        }

        await MigrarAsync(destino: null);

        await using var verificacion = new NpgsqlConnection(_cadenaConexion);
        await verificacion.OpenAsync();
        await using var leer = new NpgsqlCommand(
            """
            SELECT (SELECT relforcerowsecurity FROM pg_class WHERE relname = 'SesionesPrivilegiadas'),
                   (SELECT relforcerowsecurity FROM pg_class WHERE relname = 'ConcesionesPrivilegio');
            """, verificacion);
        await using (var lector = await leer.ExecuteReaderAsync())
        {
            await lector.ReadAsync();
            lector.GetBoolean(0).Should().BeTrue("el relleno no deja SesionesPrivilegiadas sin FORCE");
            lector.GetBoolean(1).Should().BeTrue("el relleno no deja ConcesionesPrivilegio sin FORCE");
        }

        // Leer como propietario exige ahora el contexto del titular: la RLS vuelve a mandar.
        await using var capacidad = new NpgsqlCommand(
            """
            SELECT set_config('app.usuario_id', @tecnico::text, false);
            SELECT "Capacidad" FROM "SesionesPrivilegiadas" WHERE "Id" = @sesion;
            """, verificacion);
        capacidad.Parameters.AddWithValue("tecnico", tecnico);
        capacidad.Parameters.AddWithValue("sesion", sesion);
        await using var lectorCapacidad = await capacidad.ExecuteReaderAsync();
        await lectorCapacidad.NextResultAsync();
        (await lectorCapacidad.ReadAsync()).Should().BeTrue("control positivo: el titular ve su sesión");
        lectorCapacidad.GetString(0).Should().Be("Aprovisionamiento",
            "la sesión preexistente toma la capacidad de su concesión, no la cadena vacía");
    }

    private async Task MigrarAsync(string? destino)
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;
        await using var contexto = new CaeManagerDbContext(
            options, new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = null });
        await contexto.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(destino);
    }
}
