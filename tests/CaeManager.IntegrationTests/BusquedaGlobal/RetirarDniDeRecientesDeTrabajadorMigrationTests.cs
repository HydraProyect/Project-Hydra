using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.BusquedaGlobal;

/// <summary>
/// Migración <c>RetirarDniDeRecientesDeTrabajador</c> (2026-09-24): hasta esa
/// fecha el Command Palette guardaba el DNI del Trabajador como subtítulo de
/// cada reciente. La migración lo retira en todos los Tenants y no toca los
/// recientes de otros tipos.
///
/// <para>
/// La conexión de pruebas es superusuario, que ignora RLS: con ella sola, un
/// <c>UPDATE</c> sin recorrer Tenants daría el mismo verde que el bueno. Por eso
/// el segundo test ejecuta el mismo SQL autenticando como <c>cae_app_runtime</c>,
/// que sí está sujeto a las políticas de aislamiento — sin el recorrido por
/// Tenants no actualizaría ninguna fila. Sigue sin observarse el rol propietario
/// real con <c>FORCE ROW LEVEL SECURITY</c>; el runtime es el sustituto más
/// cercano que ofrece el arnés.
/// </para>
/// </summary>
public class RetirarDniDeRecientesDeTrabajadorMigrationTests : IAsyncLifetime
{
    private const string MigracionAntes = "AgregarCapacidadOperadorCaeExternoATenant";
    private const string MigracionObjetivo = "RetirarDniDeRecientesDeTrabajador";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuario = Guid.NewGuid();

    private Guid _tenantA;
    private Guid _tenantB;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrador.MigrateAsync(MigracionAntes);

        var tenantA = new Tenant("Tenant A de prueba");
        var tenantB = new Tenant("Tenant B de prueba");
        contexto.Tenants.AddRange(tenantA, tenantB);
        await contexto.SaveChangesAsync();
        _tenantA = tenantA.Id;
        _tenantB = tenantB.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task La_migracion_retira_el_subtitulo_de_los_recientes_de_trabajador_en_todos_los_tenants()
    {
        var sembrados = await SembrarAsync();

        await using (var contexto = CrearContexto())
        {
            var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrador.MigrateAsync(MigracionObjetivo);
        }

        await ComprobarAsync(sembrados);
    }

    [Fact]
    public async Task El_sql_de_la_migracion_alcanza_todos_los_tenants_con_un_rol_sujeto_a_rls()
    {
        await using (var contexto = CrearContexto())
            await contexto.Database.MigrateAsync();

        var sembrados = await SembrarAsync();

        await using (var runtime = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion)))
        {
            await runtime.OpenAsync();
            await using var transaccion = await runtime.BeginTransactionAsync();
            await using (var comando = new NpgsqlCommand(
                             RetirarDniDeRecientesDeTrabajador.SqlRetirarSubtituloDeTrabajador, runtime, transaccion))
                await comando.ExecuteNonQueryAsync();
            await transaccion.CommitAsync();
        }

        await ComprobarAsync(sembrados);
    }

    private sealed record Sembrados(Guid TrabajadorA, Guid TrabajadorB, Guid CentroA, Guid AccionB);

    private async Task<Sembrados> SembrarAsync()
    {
        var sembrados = new Sembrados(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        async Task InsertarAsync(Guid id, Guid tenant, string tipo, string titulo, string? subtitulo, string url)
        {
            await using var comando = new NpgsqlCommand(
                """
                INSERT INTO "EventosRecientesUsuario"
                    ("Id", "TenantId", "UsuarioId", "Tipo", "EntidadId", "Titulo", "Subtitulo", "UrlDestino", "OcurridoEnUtc")
                VALUES (@id, @tenant, @usuario, @tipo, NULL, @titulo, @subtitulo, @url, now())
                """, conexion);
            comando.Parameters.AddWithValue("id", id);
            comando.Parameters.AddWithValue("tenant", tenant);
            comando.Parameters.AddWithValue("usuario", _usuario);
            comando.Parameters.AddWithValue("tipo", tipo);
            comando.Parameters.AddWithValue("titulo", titulo);
            comando.Parameters.AddWithValue("subtitulo", (object?)subtitulo ?? DBNull.Value);
            comando.Parameters.AddWithValue("url", url);
            await comando.ExecuteNonQueryAsync();
        }

        await InsertarAsync(sembrados.TrabajadorA, _tenantA, "Trabajador", "Juan Pérez", "12345678Z", "/trabajadores/a");
        await InsertarAsync(sembrados.TrabajadorB, _tenantB, "Trabajador", "Ana López", "87654321X", "/trabajadores/b");
        await InsertarAsync(sembrados.CentroA, _tenantA, "Centro", "Centro Norte", "Centro", "/centros/a");
        await InsertarAsync(sembrados.AccionB, _tenantB, "Accion", "Nuevo trabajador", "Trabajador", "/trabajadores?accion=crear");

        return sembrados;
    }

    private async Task ComprobarAsync(Sembrados sembrados)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        async Task<string?> SubtituloAsync(Guid id)
        {
            await using var comando = new NpgsqlCommand(
                """SELECT "Subtitulo" FROM "EventosRecientesUsuario" WHERE "Id" = @id""", conexion);
            comando.Parameters.AddWithValue("id", id);
            var valor = await comando.ExecuteScalarAsync();
            valor.Should().NotBeNull("el reciente sembrado tiene que seguir existiendo: la migración no borra filas");
            return valor is DBNull ? null : (string)valor!;
        }

        (await SubtituloAsync(sembrados.TrabajadorA)).Should().BeNull("el DNI del Trabajador del Tenant A se retira");
        (await SubtituloAsync(sembrados.TrabajadorB)).Should().BeNull("y el del Tenant B también: la migración recorre todos los Tenants");
        (await SubtituloAsync(sembrados.CentroA)).Should().Be("Centro", "un reciente de otro tipo no se toca");
        (await SubtituloAsync(sembrados.AccionB)).Should().Be("Trabajador",
            "el filtro es por Tipo, no por el texto del subtítulo");
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = Guid.NewGuid() };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
