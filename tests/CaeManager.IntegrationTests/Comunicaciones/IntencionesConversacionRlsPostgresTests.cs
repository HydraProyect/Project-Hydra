using CaeManager.Domain.Comunicaciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

public class IntencionesConversacionRlsPostgresTests : IAsyncLifetime
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

        var conversacion = new Conversacion("Hilo con intención");
        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cae@example.test", "Coordinación de acceso");
        dbContext.Conversaciones.Add(conversacion);
        await dbContext.SaveChangesAsync();
        _conversacionA = conversacion.Id;

        dbContext.IntencionesConversacion.Add(new IntencionConversacion(_conversacionA, mensaje.Id, IntencionCae.CoordinacionAcceso, ConfianzaIntencion.Alta));
        await dbContext.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_rol_de_ejecucion_solo_ve_las_intenciones_del_tenant_de_sesion()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);
        (await ContarAsync(conexion, "IntencionesConversacion")).Should().Be(1, "control positivo: el tenant propietario ve su intención");

        await FijarTenantAsync(conexion, _tenantB);
        (await ContarAsync(conexion, "IntencionesConversacion")).Should().Be(0);

        await FijarTenantAsync(conexion, null);
        (await ContarAsync(conexion, "IntencionesConversacion")).Should().Be(0, "sin tenant de sesión la política oculta todo");
    }

    [Fact]
    public async Task El_rol_de_ejecucion_no_inserta_una_intencion_con_el_TenantId_de_otro_tenant()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantB);

        await using var insercion = conexion.CreateCommand();
        insercion.CommandText = """
            INSERT INTO "IntencionesConversacion" ("Id", "ConversacionId", "MensajeId", "IntencionPropuesta", "IntencionActual", "Confianza", "ConfirmadaPorPersona", "TenantId")
            VALUES (@id, @conversacion, @autor, 1, 1, 3, false, @tenant);
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
    public async Task El_filtro_EF_no_expone_la_intencion_de_otro_Tenant_propietario()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantB };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(_cadenaConexion).Options;
        await using var db = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
        (await db.IntencionesConversacion.CountAsync()).Should().Be(0);
        (await db.IntencionesConversacion.IgnoreQueryFilters().CountAsync()).Should().Be(1);
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
