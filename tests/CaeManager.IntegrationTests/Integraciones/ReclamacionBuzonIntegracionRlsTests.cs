using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Integraciones;

public class ReclamacionBuzonIntegracionRlsTests : IAsyncLifetime
{
    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _conexionA = Guid.NewGuid();
    private Guid _idA;
    private Guid _idB;

    public async Task InitializeAsync()
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;
        await using var contexto = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), new TenantActualAmbiental());
        await contexto.Database.MigrateAsync();
        var a = new ReclamacionBuzonIntegracion("a@example.com", _tenantA, _conexionA, DateTime.UtcNow);
        var b = new ReclamacionBuzonIntegracion("b@example.com", _tenantB, Guid.NewGuid(), DateTime.UtcNow);
        contexto.ReclamacionesBuzonIntegracion.AddRange(a, b);
        await contexto.SaveChangesAsync();
        _idA = a.Id;
        _idB = b.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Theory]
    [InlineData("cae_app_runtime")]
    [InlineData("cae_app_soporte")]
    public async Task Cada_rol_ve_solo_el_Tenant_propietario_del_contexto(string rol)
    {
        await using var conexion = await AbrirAsync(rol, _tenantA);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT \"Id\" FROM public.\"ReclamacionesBuzonIntegracion\"";
        var ids = new List<Guid>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync()) ids.Add(lector.GetGuid(0));
        ids.Should().BeEquivalentTo([_idA]);
        ids.Should().NotContain(_idB);
    }

    [Fact]
    public async Task Sin_Tenant_no_hay_filas_visibles()
    {
        await using var conexion = await AbrirAsync("cae_app_runtime", null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM public.\"ReclamacionesBuzonIntegracion\"";
        ((long)(await comando.ExecuteScalarAsync())!).Should().Be(0);
    }

    [Fact]
    public async Task La_escritura_ajena_se_rechaza_y_la_unicidad_global_permanece()
    {
        await using var conexion = await AbrirAsync("cae_app_runtime", _tenantB);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            INSERT INTO public."ReclamacionesBuzonIntegracion"
                ("Id", "BuzonEmail", "TenantPropietarioId", "ConexionIntegracionId", "ReclamadoEnUtc")
            VALUES (@id, @email, @tenant, @conexion, now());
            """;
        comando.Parameters.AddWithValue("id", Guid.NewGuid());
        comando.Parameters.AddWithValue("email", "nuevo@example.com");
        comando.Parameters.AddWithValue("tenant", _tenantA);
        comando.Parameters.AddWithValue("conexion", Guid.NewGuid());
        var insertar = async () => await comando.ExecuteNonQueryAsync();
        (await insertar.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        comando.Parameters["tenant"].Value = _tenantB;
        comando.Parameters["email"].Value = "a@example.com";
        (await insertar.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);

        comando.Parameters["email"].Value = "nuevo@example.com";
        (await comando.ExecuteNonQueryAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Solo_el_Tenant_propietario_puede_liberar_su_reclamacion()
    {
        await using var conexion = await AbrirAsync("cae_app_runtime", _tenantB);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "DELETE FROM public.\"ReclamacionesBuzonIntegracion\" WHERE \"Id\" = @id";
        comando.Parameters.AddWithValue("id", _idA);
        (await comando.ExecuteNonQueryAsync()).Should().Be(0);
        comando.Parameters["id"].Value = _idB;
        (await comando.ExecuteNonQueryAsync()).Should().Be(1);
    }

    /// <summary>
    /// El repositorio distingue la guarda de buzón por el nombre del índice: aquí se comprueba que
    /// el 23505 bajo <c>cae_app_runtime</c> y RLS conserva ese nombre y llega como "buzón ocupado",
    /// no como excepción. Con mayúsculas, porque la unicidad se apoya en la normalización de la entidad.
    /// </summary>
    [Fact]
    public async Task El_repositorio_traduce_el_choque_con_un_buzon_ajeno_que_no_puede_ver()
    {
        await using var conexion = await AbrirAsync("cae_app_runtime", _tenantB);
        await using var contexto = CrearContexto(conexion);
        var repositorio = new ReclamacionBuzonIntegracionRepository(contexto);

        repositorio.Reclamar(new ReclamacionBuzonIntegracion("A@Example.com", _tenantB, Guid.NewGuid(), DateTime.UtcNow));

        (await repositorio.GuardarCambiosSiBuzonLibreAsync()).Should().BeFalse();
    }

    /// <summary>
    /// Liberar no falla si la fila no es visible: sin esta prueba, un contexto equivocado dejaría
    /// el buzón reclamado para siempre sin ningún error.
    /// </summary>
    [Fact]
    public async Task El_repositorio_libera_con_el_Tenant_propietario_y_no_toca_la_reclamacion_ajena()
    {
        await using (var conexionAjena = await AbrirAsync("cae_app_runtime", _tenantB))
        {
            await using var contextoAjeno = CrearContexto(conexionAjena);
            await new ReclamacionBuzonIntegracionRepository(contextoAjeno).LiberarAsync(_conexionA);
            await contextoAjeno.SaveChangesAsync();
        }

        await using var conexion = await AbrirAsync("cae_app_runtime", _tenantA);
        await using var contexto = CrearContexto(conexion);
        (await contexto.ReclamacionesBuzonIntegracion.CountAsync(r => r.Id == _idA)).Should().Be(1);

        await new ReclamacionBuzonIntegracionRepository(contexto).LiberarAsync(_conexionA);
        await contexto.SaveChangesAsync();

        (await contexto.ReclamacionesBuzonIntegracion.CountAsync(r => r.Id == _idA)).Should().Be(0);
    }

    private static CaeManagerDbContext CrearContexto(NpgsqlConnection conexion) =>
        new(new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(conexion).Options,
            new EphemeralDataProtectionProvider(), new TenantActualAmbiental());

    private async Task<NpgsqlConnection> AbrirAsync(string rol, Guid? tenant)
    {
        var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: tenant);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"SET ROLE {rol};";
        await comando.ExecuteNonQueryAsync();
        return conexion;
    }
}
