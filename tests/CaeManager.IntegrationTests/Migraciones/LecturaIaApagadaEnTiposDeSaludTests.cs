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
    private const string MigracionDelCambio = "20261101040000_LecturaIaApagadaEnTiposDeSalud";
    private const string MigracionProteccion = "20261103000000_ProtegeLecturaIaInicialSalud";
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

        await contexto.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS proteger_lectura_ia_inicial_salud ON \"TiposDocumento\"; DROP FUNCTION IF EXISTS proteger_lectura_ia_inicial_salud();");

        contexto.Tenants.Add(_tenant);
        await contexto.SaveChangesAsync();

        await SembrarCopiaAsync(_deSalud, "Aptitud del Tenant", "CategoriaEspecialSalud");
        await SembrarCopiaAsync(_sinSalud, "Formación del Tenant", "DatosPersonales");
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task Apaga_la_IA_de_los_tipos_de_salud_de_cualquier_Tenant_y_solo_de_esos()
    {
        (await LeerAsync(_deSalud)).Should().Be((true, true, true), "guarda del instrumento: la copia parte encendida");

        await using (var contexto = NuevoContexto())
            await contexto.GetService<IMigrator>().MigrateAsync(MigracionDelCambio);

        (await LeerAsync(_deSalud)).Should().Be((false, false, true));
        (await LeerAsync(_sinSalud)).Should().Be((true, true, true));
    }

    [Fact]
    public async Task Protege_altas_y_cambios_a_salud_y_permite_activar_despues_incluso_tras_bajar()
    {
        await using var contexto = NuevoContexto();
        var migrador = contexto.GetService<IMigrator>();
        await migrador.MigrateAsync(MigracionDelCambio);
        await EjecutarAsync("UPDATE \"TiposDocumento\" SET \"LecturaIaActiva\" = true, \"VerificacionIaActiva\" = true WHERE \"Id\" = @id", _deSalud);
        await migrador.MigrateAsync(MigracionProteccion);
        (await LeerAsync(_deSalud)).Should().Be((false, false, false), "la primera aplicación cierra la ventana del relevo");
        (await LeerAsync(_sinSalud)).Should().Be((true, true, true));

        // Una versión anterior intenta insertar una fila con los tres indicadores encendidos tras instalar la protección.
        var duranteRelevo = Guid.NewGuid();
        await SembrarCopiaAsync(duranteRelevo, "Durante relevo", "CategoriaEspecialSalud");
        (await LeerAsync(duranteRelevo)).Should().Be((false, false, false));

        var nuevaSalud = Guid.NewGuid();
        var nuevaNoSalud = Guid.NewGuid();
        await SembrarCopiaAsync(nuevaSalud, "Alta salud", "CategoriaEspecialSalud");
        await SembrarCopiaAsync(nuevaNoSalud, "Alta no salud", "DatosPersonales");
        (await LeerAsync(nuevaSalud)).Should().Be((false, false, false));
        (await LeerAsync(nuevaNoSalud)).Should().Be((true, true, true));

        await EjecutarAsync("UPDATE \"TiposDocumento\" SET \"Sensibilidad\" = 'CategoriaEspecialSalud' WHERE \"Id\" = @id", nuevaNoSalud);
        (await LeerAsync(nuevaNoSalud)).Should().Be((false, false, false));

        var cambioYActivacion = Guid.NewGuid();
        await SembrarCopiaAsync(cambioYActivacion, "Cambio y activación", "DatosPersonales");
        await EjecutarAsync("UPDATE \"TiposDocumento\" SET \"LecturaIaActiva\" = false, \"VerificacionIaActiva\" = false WHERE \"Id\" = @id", cambioYActivacion);
        await EjecutarAsync("UPDATE \"TiposDocumento\" SET \"Sensibilidad\" = 'CategoriaEspecialSalud', \"LecturaIaActiva\" = true, \"VerificacionIaActiva\" = true WHERE \"Id\" = @id", cambioYActivacion);
        (await LeerAsync(cambioYActivacion)).Should().Be((false, false, false));

        await EjecutarAsync("UPDATE \"TiposDocumento\" SET \"VerificacionIaActiva\" = true WHERE \"Id\" = @id", nuevaSalud);
        (await LeerAsync(nuevaSalud)).Should().Be((false, true, false));
        await EjecutarAsync("UPDATE \"TiposDocumento\" SET \"LecturaIaActiva\" = true, \"VerificacionIaActiva\" = true WHERE \"Id\" = @id", nuevaSalud);
        (await LeerAsync(nuevaSalud)).Should().Be((true, true, false));
        await EjecutarAsync("UPDATE \"TiposDocumento\" SET \"Nombre\" = 'Actualizado' WHERE \"Id\" = @id", nuevaSalud);
        (await LeerAsync(nuevaSalud)).Should().Be((true, true, false));

        var anterior = contexto.Database.GetMigrations().ToList();
        await migrador.MigrateAsync(anterior[anterior.IndexOf(MigracionProteccion) - 1]);
        var trasBajar = Guid.NewGuid();
        await SembrarCopiaAsync(trasBajar, "Tras bajar", "CategoriaEspecialSalud");
        (await LeerAsync(trasBajar)).Should().Be((false, false, false));
        await EjecutarAsync("UPDATE \"TiposDocumento\" SET \"LecturaIaActiva\" = true, \"VerificacionIaActiva\" = true, \"DeteccionTrabajadoresActiva\" = true WHERE \"Id\" = @id", _deSalud);
        await migrador.MigrateAsync(MigracionProteccion);
        (await LeerAsync(_deSalud)).Should().Be((true, true, true), "reaplicar conserva la activación deliberada tras bajar");
        (await LeerAsync(nuevaSalud)).Should().Be((true, true, false));
    }

    private async Task EjecutarAsync(string sql, Guid id)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexion);
        comando.Parameters.AddWithValue("id", id);
        (await comando.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private async Task SembrarCopiaAsync(Guid id, string nombre, string sensibilidad)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(
            """
            CREATE TEMP TABLE copia AS SELECT * FROM "TiposDocumento" WHERE "Id" = @base::uuid;
            UPDATE copia SET "Id" = @id, "TenantId" = @tenant, "Nombre" = @nombre, "Sensibilidad" = @sensibilidad,
                "LecturaIaActiva" = true, "VerificacionIaActiva" = true, "DeteccionTrabajadoresActiva" = true;
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

    private async Task<(bool Lectura, bool Verificacion, bool Deteccion)> LeerAsync(Guid id)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(
            """SELECT "LecturaIaActiva", "VerificacionIaActiva", "DeteccionTrabajadoresActiva" FROM "TiposDocumento" WHERE "Id" = @id""", conexion);
        comando.Parameters.AddWithValue("id", id);
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();
        return (lector.GetBoolean(0), lector.GetBoolean(1), lector.GetBoolean(2));
    }

    private CaeManagerDbContext NuevoContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = null };
        var opciones = ModeloSinColumnasDeConectores.Opciones()
            .UseNpgsql(_cadena, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual), new ConcurrenciaOptimistaInterceptor())
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
