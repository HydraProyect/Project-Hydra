using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// La migración <c>LecturaIaApagadaEnTiposDeSalud</c> sobre una base que ya tiene un tipo de salud encendido en otro
/// Tenant: esa fila solo la alcanza el SQL propio de la migración, no el <c>UpdateData</c> de <c>HasData</c>. Las filas se
/// copian de la de aptitud del catálogo para no repetir aquí las columnas de la tabla.
/// </summary>
public class LecturaIaApagadaEnTiposDeSaludTests : IAsyncLifetime
{
    private const string MigracionDelCambio = "20261101020000_LecturaIaApagadaEnTiposDeSalud";
    private const string BaseAptitud = "10000000-0000-0000-0000-000000000001";

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _tenant = new("Tenant con tipos encendidos");
    private readonly Guid _deSalud = Guid.NewGuid();
    private readonly Guid _sinSalud = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadena);

        await using var contexto = NuevoContexto();
        var migraciones = contexto.Database.GetMigrations().ToList();
        var indice = migraciones.IndexOf(MigracionDelCambio);
        indice.Should().BePositive("la migración del cambio existe en el ensamblado");
        await contexto.GetService<IMigrator>().MigrateAsync(migraciones[indice - 1]);

        contexto.Tenants.Add(_tenant);
        await contexto.SaveChangesAsync();

        await SembrarCopiaAsync(_deSalud, "Aptitud del Tenant", "CategoriaEspecialSalud");
        await SembrarCopiaAsync(_sinSalud, "Formación del Tenant", "DatosPersonales");
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task Apaga_la_IA_de_los_tipos_de_salud_de_cualquier_Tenant_y_solo_de_esos()
    {
        (await LeerAsync(_deSalud)).Should().Be((true, true), "guarda del instrumento: la copia parte encendida");

        await using (var contexto = NuevoContexto())
            await contexto.GetService<IMigrator>().MigrateAsync(MigracionDelCambio);

        (await LeerAsync(_deSalud)).Should().Be((false, false));
        (await LeerAsync(_sinSalud)).Should().Be((true, true));
    }

    private async Task SembrarCopiaAsync(Guid id, string nombre, string sensibilidad)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(
            """
            CREATE TEMP TABLE copia AS SELECT * FROM "TiposDocumento" WHERE "Id" = @base::uuid;
            UPDATE copia SET "Id" = @id, "TenantId" = @tenant, "Nombre" = @nombre, "Sensibilidad" = @sensibilidad,
                "LecturaIaActiva" = true, "VerificacionIaActiva" = true;
            INSERT INTO "TiposDocumento" SELECT * FROM copia;
            DROP TABLE copia;
            """, conexion);
        comando.Parameters.AddWithValue("base", BaseAptitud);
        comando.Parameters.AddWithValue("id", id);
        comando.Parameters.AddWithValue("tenant", _tenant.Id);
        comando.Parameters.AddWithValue("nombre", nombre);
        comando.Parameters.AddWithValue("sensibilidad", sensibilidad);
        (await comando.ExecuteNonQueryAsync()).Should().BePositive();
    }

    private async Task<(bool Lectura, bool Verificacion)> LeerAsync(Guid id)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(
            """SELECT "LecturaIaActiva", "VerificacionIaActiva" FROM "TiposDocumento" WHERE "Id" = @id""", conexion);
        comando.Parameters.AddWithValue("id", id);
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();
        return (lector.GetBoolean(0), lector.GetBoolean(1));
    }

    private CaeManagerDbContext NuevoContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = null };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual), new ConcurrenciaOptimistaInterceptor())
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
