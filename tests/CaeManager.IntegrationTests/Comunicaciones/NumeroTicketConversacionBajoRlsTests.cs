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
/// Número de ticket bajo los roles reales: correlativo por Tenant propietario, asignado por el disparador aunque
/// el código no lo mande, y con el contador aislado por RLS. <c>SET ROLE</c> desde el superusuario de los tests,
/// como en <c>NotasInternasRlsPostgresTests</c>, porque RLS no restringe a quien la salta.
/// </summary>
public class NumeroTicketConversacionBajoRlsTests : IAsyncLifetime
{
    private const string PermisoDenegado = "42501";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    public Task InitializeAsync() => BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_contador_tiene_RLS_forzada_y_la_politica_de_aislamiento()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT c.relrowsecurity AND c.relforcerowsecurity,
                   (SELECT count(*) FROM pg_policies p
                     WHERE p.tablename = c.relname AND p.policyname = 'aislamiento_tenant' AND p.with_check IS NOT NULL)
            FROM pg_class c
            WHERE c.oid = 'public."ContadoresTicketConversacion"'::regclass;
            """;
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();

        lector.GetBoolean(0).Should().BeTrue("ENABLE y FORCE ROW LEVEL SECURITY");
        lector.GetInt64(1).Should().Be(1, "la política de aislamiento con WITH CHECK explícito");
    }

    [Fact]
    public async Task Cada_Tenant_numera_desde_uno_y_sin_huecos_por_el_camino_de_EF()
    {
        await using var contextoA = CrearContexto(_tenantA);
        var primera = new Conversacion("Primera del Tenant A");
        var segunda = new Conversacion("Segunda del Tenant A");
        contextoA.Conversaciones.AddRange(primera, segunda);
        await contextoA.SaveChangesAsync();

        await using var contextoB = CrearContexto(_tenantB);
        var deOtroTenant = new Conversacion("Primera del Tenant B");
        contextoB.Conversaciones.Add(deOtroTenant);
        await contextoB.SaveChangesAsync();

        new[] { primera.NumeroTicket, segunda.NumeroTicket }.Should().BeEquivalentTo(new long[] { 1, 2 },
            "EF lee de RETURNING el número que asigna el disparador");
        deOtroTenant.NumeroTicket.Should().Be(1, "el correlativo es por Tenant propietario, no global");
    }

    [Fact]
    public async Task El_disparador_ignora_el_numero_que_mande_quien_inserta()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);

        var numero = await InsertarConversacionAsync(conexion, _tenantA, numeroPedido: 999);

        numero.Should().Be(1);
    }

    [Fact]
    public async Task La_ranura_anterior_inserta_sin_conocer_la_columna()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);

        var numero = await InsertarConversacionAsync(conexion, _tenantA, numeroPedido: null);

        numero.Should().Be(1, "el INSERT sin la columna es el de la versión anterior durante el relevo");
    }

    [Fact]
    public async Task Un_rollback_devuelve_el_numero_y_no_deja_hueco()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);
        await InsertarConversacionAsync(conexion, _tenantA, numeroPedido: null);

        await using (var transaccion = await conexion.BeginTransactionAsync())
        {
            (await InsertarConversacionAsync(conexion, _tenantA, numeroPedido: null)).Should().Be(2);
            await transaccion.RollbackAsync();
        }

        (await InsertarConversacionAsync(conexion, _tenantA, numeroPedido: null)).Should().Be(2);
    }

    [Fact]
    public async Task Runtime_no_abre_una_conversacion_en_otro_Tenant_ni_mueve_su_contador()
    {
        await using (var propia = await AbrirComoAsync("cae_app_runtime", _tenantB))
        {
            await InsertarConversacionAsync(propia, _tenantB, numeroPedido: null);
        }

        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);
        var excepcion = await Record.ExceptionAsync(() => InsertarConversacionAsync(conexion, _tenantB, numeroPedido: null));

        excepcion.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PermisoDenegado);

        await using var delTenantB = await AbrirComoAsync("cae_app_runtime", _tenantB);
        (await InsertarConversacionAsync(delTenantB, _tenantB, numeroPedido: null)).Should().Be(2,
            "el intento desde el Tenant A no consumió ningún número del Tenant B");
    }

    [Fact]
    public async Task Runtime_solo_ve_su_contador_y_no_puede_borrarlo()
    {
        await using var delTenantB = await AbrirComoAsync("cae_app_runtime", _tenantB);
        await InsertarConversacionAsync(delTenantB, _tenantB, numeroPedido: null);

        await using var conexion = await AbrirComoAsync("cae_app_runtime", _tenantA);
        await InsertarConversacionAsync(conexion, _tenantA, numeroPedido: null);

        (await EjecutarEscalarAsync(conexion, """SELECT count(*) FROM "ContadoresTicketConversacion";""")).Should().Be(1L);

        var excepcion = await Record.ExceptionAsync(() => EjecutarEscalarAsync(conexion, """DELETE FROM "ContadoresTicketConversacion";"""));

        excepcion.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PermisoDenegado,
            "borrar el contador reutilizaría números ya entregados");
    }

    [Fact]
    public async Task Soporte_TALVEG_no_lee_el_contador_pero_si_las_conversaciones()
    {
        await using var conexion = await AbrirComoAsync("cae_app_soporte", _tenantA);

        (await EjecutarEscalarAsync(conexion, """SELECT count(*) FROM "Conversaciones";""")).Should().Be(0L,
            "control positivo: el rol lee la tabla de negocio, así que la denegación es del contador");

        var excepcion = await Record.ExceptionAsync(() => EjecutarEscalarAsync(conexion, """SELECT count(*) FROM "ContadoresTicketConversacion";"""));

        excepcion.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PermisoDenegado);
    }

    private CaeManagerDbContext CrearContexto(Guid tenant)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private async Task<NpgsqlConnection> AbrirComoAsync(string rol, Guid tenant)
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using (var setRol = conexion.CreateCommand())
        {
            setRol.CommandText = $"SET ROLE {rol};";
            await setRol.ExecuteNonQueryAsync();
        }

        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: tenant);
        return conexion;
    }

    private static async Task<long> InsertarConversacionAsync(NpgsqlConnection conexion, Guid tenant, long? numeroPedido)
    {
        await using var comando = conexion.CreateCommand();
        var (columna, valor) = numeroPedido is null ? ("", "") : (", \"NumeroTicket\"", $", {numeroPedido}");
        comando.CommandText = $"""
            INSERT INTO "Conversaciones" ("Id", "TenantId", "Asunto", "Estado", "EstadoDesdeUtc", "FechaUltimoMensajeUtc",
                                          "Canal", "Prioridad", "CreadoEnUtc", "EstaEliminado", "Version"{columna})
            VALUES (@id, @tenant, 'Hilo', 0, now(), now(), 0, 0, now(), false, @version{valor})
            RETURNING "NumeroTicket";
            """;
        comando.Parameters.AddWithValue("id", Guid.NewGuid());
        comando.Parameters.AddWithValue("version", Guid.NewGuid());
        comando.Parameters.AddWithValue("tenant", tenant);
        return (long)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task<object?> EjecutarEscalarAsync(NpgsqlConnection conexion, string sql)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        return await comando.ExecuteScalarAsync();
    }
}
