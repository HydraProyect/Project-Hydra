using CaeManager.Domain.Comunicaciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// Capa PostgreSQL de las notas internas: lo que se sostiene aunque la capa de
/// aplicación fallara. Dos propiedades distintas, cada una con su rol real
/// (mismo método que <c>AislamientoRlsPostgresTests</c>: <c>SET ROLE</c> desde
/// el superusuario de los tests, porque RLS no restringe al propietario):
/// <list type="bullet">
/// <item><c>cae_app_runtime</c> solo ve y solo escribe las notas del tenant de
/// sesión (política <c>aislamiento_tenant</c>, USING y WITH CHECK).</item>
/// <item><c>cae_app_soporte</c> —el rol con el que lee una sesión privilegiada
/// de Soporte TALVEG— no tiene ningún privilegio sobre la tabla, ni dentro del
/// tenant objetivo (D3-Soporte). El control positivo sobre
/// <c>Conversaciones</c> demuestra que la denegación es de esta tabla y no un
/// rol inutilizable.</item>
/// </list>
/// </summary>
public class NotasInternasRlsPostgresTests : IAsyncLifetime
{
    private const string PermisoDenegado = "42501";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private Guid _conversacionA;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantA };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        await using var dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await dbContext.Database.MigrateAsync();

        var conversacion = new Conversacion("Hilo con nota");
        dbContext.Conversaciones.Add(conversacion);
        await dbContext.SaveChangesAsync();
        _conversacionA = conversacion.Id;

        dbContext.NotasInternasConversacion.Add(new NotaInternaConversacion(_conversacionA, Guid.NewGuid(), "Nota del equipo.", DateTime.UtcNow));
        await dbContext.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_rol_de_ejecucion_solo_ve_las_notas_del_tenant_de_sesion()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);
        (await ContarAsync(conexion, "NotasInternasConversacion")).Should().Be(1, "control positivo: el tenant propietario ve su nota");

        await FijarTenantAsync(conexion, _tenantB);
        (await ContarAsync(conexion, "NotasInternasConversacion")).Should().Be(0);

        await FijarTenantAsync(conexion, null);
        (await ContarAsync(conexion, "NotasInternasConversacion")).Should().Be(0, "sin tenant de sesión la política oculta todo");
    }

    [Fact]
    public async Task El_rol_de_ejecucion_no_inserta_una_nota_con_el_TenantId_de_otro_tenant()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantB);

        await using var insercion = conexion.CreateCommand();
        insercion.CommandText = """
            INSERT INTO "NotasInternasConversacion" ("Id", "ConversacionId", "AutorUsuarioId", "Texto", "FechaUtc", "TenantId")
            VALUES (@id, @conversacion, @autor, 'Nota colada', now(), @tenant);
            """;
        insercion.Parameters.AddWithValue("id", Guid.NewGuid());
        insercion.Parameters.AddWithValue("conversacion", _conversacionA);
        insercion.Parameters.AddWithValue("autor", Guid.NewGuid());
        insercion.Parameters.AddWithValue("tenant", _tenantA);

        var excepcion = await Record.ExceptionAsync(() => insercion.ExecuteNonQueryAsync());

        excepcion.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PermisoDenegado,
            "WITH CHECK de aislamiento_tenant rechaza una fila cuyo TenantId no es el de la sesión");
    }

    [Fact]
    public async Task El_rol_de_Soporte_TALVEG_no_lee_notas_ni_dentro_del_tenant_objetivo()
    {
        await using var conexion = await AbrirComoAsync("cae_app_soporte", _tenantA);

        (await ContarAsync(conexion, "Conversaciones")).Should().Be(1,
            "control positivo: el rol de soporte sí lee el resto del tenant objetivo, así que la denegación es de esta tabla");

        var excepcion = await Record.ExceptionAsync(() => ContarAsync(conexion, "NotasInternasConversacion"));

        excepcion.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PermisoDenegado,
            "D3-Soporte: Soporte TALVEG no lee notas internas; la migración le revoca todo privilegio sobre la tabla");
    }

    private async Task<NpgsqlConnection> AbrirComoAsync(string rol, Guid? tenant)
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await using (var setRol = conexion.CreateCommand())
        {
            setRol.CommandText = $"SET ROLE {rol};";
            await setRol.ExecuteNonQueryAsync();
        }

        await FijarTenantAsync(conexion, tenant);
        return conexion;
    }

    private static async Task FijarTenantAsync(NpgsqlConnection conexion, Guid? tenantId)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT set_config('app.tenant_id', @valor, false);";
        comando.Parameters.AddWithValue("valor", tenantId?.ToString() ?? string.Empty);
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, string tabla)
    {
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = $"SELECT count(*) FROM \"{tabla}\";";
        return (long)(await consulta.ExecuteScalarAsync())!;
    }
}
