using CaeManager.Domain.Integraciones;
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

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// <see cref="BackfillReclamacionBuzonIntegracionDesdeConexionesExistentes"/>
/// (fusionada en PR #869, mismo día) leía <c>ConexionesIntegracion</c> con un
/// <c>SELECT</c> plano. Esa tabla tiene <c>FORCE ROW LEVEL SECURITY</c> desde
/// <c>HabilitarRlsIntegraciones</c> (2026-08-02), y el rol que migra en
/// producción es el propietario, sin <c>BYPASSRLS</c>: sin <c>app.tenant_id</c>
/// fijado, la política <c>aislamiento_tenant</c> no empareja ninguna fila y el
/// backfill original insertó CERO filas reales, aunque sus tests dieran verde
/// — la conexión de pruebas de la migración es superusuario, que ignora RLS
/// (mismo patrón que <c>RetirarDniDeRecientesDeTrabajadorMigrationTests</c>).
///
/// <para>
/// La tabla se limpia antes de ejercer el SQL de la corrección para aislar lo
/// que ESE SQL, bajo el rol runtime (el sustituto más cercano al propietario
/// real sujeto a RLS que ofrece el arnés), es capaz de ver y reconstruir por
/// sí mismo — el backfill original, aplicado en <see cref="InitializeAsync"/>
/// bajo el superusuario del arnés, ya habría insertado las filas y ocultaría
/// el defecto si no se limpiara antes.
/// </para>
/// </summary>
public class CorregirBackfillReclamacionBuzonIntegracionBajoRlsTests : IAsyncLifetime
{
    private const string MigracionAntesDeLaCorreccion = "BackfillReclamacionBuzonIntegracionDesdeConexionesExistentes";
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(Guid.NewGuid());
        var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrador.MigrateAsync(MigracionAntesDeLaCorreccion);
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_sql_de_la_correccion_reclama_buzones_de_varios_tenants_con_un_rol_sujeto_a_rls()
    {
        Guid tenantA, tenantB, conexionAId, conexionBId;

        await using (var contexto = CrearContexto(Guid.NewGuid()))
        {
            var tenant = new Tenant("Tenant A de prueba");
            contexto.Tenants.Add(tenant);
            await contexto.SaveChangesAsync();
            tenantA = tenant.Id;
        }

        await using (var contexto = CrearContexto(Guid.NewGuid()))
        {
            var tenant = new Tenant("Tenant B de prueba");
            contexto.Tenants.Add(tenant);
            await contexto.SaveChangesAsync();
            tenantB = tenant.Id;
        }

        await using (var contexto = CrearContexto(tenantA))
        {
            var conexion = new ConexionIntegracion("cae@arcosspa.example", "Buzón preexistente A");
            contexto.ConexionesIntegracion.Add(conexion);
            await contexto.SaveChangesAsync();
            conexionAId = conexion.Id;
        }

        await using (var contexto = CrearContexto(tenantB))
        {
            var conexion = new ConexionIntegracion("otro@refrielectric.example", "Buzón preexistente B");
            contexto.ConexionesIntegracion.Add(conexion);
            await contexto.SaveChangesAsync();
            conexionBId = conexion.Id;
        }

        await LimpiarReclamacionesAsync();
        await EjecutarCorreccionComoRuntimeAsync();

        await using var verificacion = CrearContexto(Guid.NewGuid());
        var reclamaciones = await verificacion.ReclamacionesBuzonIntegracion.ToListAsync();

        reclamaciones.Should().HaveCount(2, "el rol runtime, sujeto a RLS, debe ver y reclamar los buzones de AMBOS Tenants");
        reclamaciones.Should().Contain(r => r.TenantPropietarioId == tenantA && r.ConexionIntegracionId == conexionAId);
        reclamaciones.Should().Contain(r => r.TenantPropietarioId == tenantB && r.ConexionIntegracionId == conexionBId);
    }

    /// <summary>
    /// El backfill original resuelve un buzón ya compartido entre dos Tenants
    /// a favor del más antiguo con un único <c>ORDER BY / ON CONFLICT</c>
    /// global (hallazgo de la ronda 2 de Codex sobre PR #820, con test
    /// propio). La corrección tiene que preservar esa garantía pese a
    /// recolectar tenant por tenant: si el desempate dependiera del orden de
    /// recorrido del bucle en vez de <c>CreadoEnUtc</c>/<c>Id</c>, este test
    /// fallaría con el Tenant equivocado.
    /// </summary>
    [Fact]
    public async Task El_desempate_determinista_se_mantiene_entre_tenants_distintos_bajo_rls()
    {
        Guid tenantAntiguo, tenantReciente, conexionAntiguaId;

        await using (var contexto = CrearContexto(Guid.NewGuid()))
        {
            var tenant = new Tenant("Tenant antiguo de prueba");
            contexto.Tenants.Add(tenant);
            await contexto.SaveChangesAsync();
            tenantAntiguo = tenant.Id;
        }

        await using (var contexto = CrearContexto(Guid.NewGuid()))
        {
            var tenant = new Tenant("Tenant reciente de prueba");
            contexto.Tenants.Add(tenant);
            await contexto.SaveChangesAsync();
            tenantReciente = tenant.Id;
        }

        await using (var contexto = CrearContexto(tenantAntiguo))
        {
            var conexion = new ConexionIntegracion("duplicado@arcosspa.example", "Conexión más antigua");
            contexto.ConexionesIntegracion.Add(conexion);
            await contexto.SaveChangesAsync();
            conexionAntiguaId = conexion.Id;
        }

        await using (var contexto = CrearContexto(tenantReciente))
        {
            contexto.ConexionesIntegracion.Add(new ConexionIntegracion("Duplicado@ArcosSPA.example", "Conexión más reciente"));
            await contexto.SaveChangesAsync();
        }

        await using (var conexionSql = new NpgsqlConnection(_cadenaConexion))
        {
            await conexionSql.OpenAsync();
            await using var comando = conexionSql.CreateCommand();
            comando.CommandText = """UPDATE "ConexionesIntegracion" SET "CreadoEnUtc" = "CreadoEnUtc" - INTERVAL '1 day' WHERE "Id" = @id""";
            comando.Parameters.AddWithValue("id", conexionAntiguaId);
            await comando.ExecuteNonQueryAsync();
        }

        await LimpiarReclamacionesAsync();
        await EjecutarCorreccionComoRuntimeAsync();

        await using var verificacion = CrearContexto(Guid.NewGuid());
        var reclamaciones = await verificacion.ReclamacionesBuzonIntegracion.ToListAsync();

        reclamaciones.Should().ContainSingle("el índice único global no puede tener dos filas para el mismo buzón");
        reclamaciones.Single().TenantPropietarioId.Should().Be(
            tenantAntiguo,
            "el desempate por fecha debe seguir siendo global entre Tenants, no depender del orden de recorrido del bucle");
    }

    private async Task LimpiarReclamacionesAsync()
    {
        await using var limpiar = new NpgsqlConnection(_cadenaConexion);
        await limpiar.OpenAsync();
        await using var comando = limpiar.CreateCommand();
        comando.CommandText = """DELETE FROM "ReclamacionesBuzonIntegracion" """;
        await comando.ExecuteNonQueryAsync();
    }

    private async Task EjecutarCorreccionComoRuntimeAsync()
    {
        await using var runtime = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion));
        await runtime.OpenAsync();
        await using var transaccion = await runtime.BeginTransactionAsync();
        await using (var comando = new NpgsqlCommand(
                         CorregirBackfillReclamacionBuzonIntegracionBajoRls.SqlCorregirBackfill, runtime, transaccion))
            await comando.ExecuteNonQueryAsync();
        await transaccion.CommitAsync();
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
