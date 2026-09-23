using CaeManager.Domain.Integraciones;
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

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// Hallazgo P1 de la revisión Codex sobre PR #820 (incremento 1 de
/// PROPUESTA-BUZONES-COMPARTIDOS-M365 § 5.4): la migración que crea
/// <c>ReclamacionesBuzonIntegracion</c> deja la tabla vacía —
/// <see cref="BackfillReclamacionBuzonIntegracionDesdeConexionesExistentes"/>
/// reconstruye las reclamaciones desde <c>ConexionesIntegracion</c>. Migra
/// hasta justo ANTES del backfill, siembra conexiones sintéticas y comprueba
/// el resultado tras avanzar hasta el final.
/// </summary>
public class BackfillReclamacionBuzonIntegracionMigrationTests : IAsyncLifetime
{
    private const string MigracionAntesDelBackfill = "AgregarReclamacionBuzonIntegracion";
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(Guid.NewGuid());
        var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrador.MigrateAsync(MigracionAntesDelBackfill);
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_backfill_reclama_los_buzones_M365_existentes_y_excluye_WhatsApp()
    {
        var tenantM365 = Guid.NewGuid();
        var tenantWhatsApp = Guid.NewGuid();
        Guid conexionM365Id;

        await using (var contexto = CrearContexto(tenantM365))
        {
            var conexion = new ConexionIntegracion("CAE@ArcosSPA.example", "Buzón CAE preexistente");
            contexto.ConexionesIntegracion.Add(conexion);
            await contexto.SaveChangesAsync();
            conexionM365Id = conexion.Id;
        }

        await using (var contexto = CrearContexto(tenantWhatsApp))
        {
            // BuzonEmail reutilizado para el número E.164 en WhatsApp (deuda
            // nominal documentada en ConexionIntegracion) — el backfill debe
            // ignorarlo: no es un buzón de correo real que proteger.
            contexto.ConexionesIntegracion.Add(
                new ConexionIntegracion("+34600111222", "Línea WhatsApp preexistente", proveedor: ProveedorIntegracion.WhatsApp));
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto(Guid.NewGuid()))
        {
            var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrador.MigrateAsync(); // hasta el final: incluye el backfill.
        }

        await using var verificacion = CrearContexto(Guid.NewGuid());
        var reclamaciones = await verificacion.ReclamacionesBuzonIntegracion.ToListAsync();

        reclamaciones.Should().ContainSingle(
            "solo la conexión M365 preexistente debe backfillearse; la de WhatsApp no es un buzón de correo");
        var reclamacion = reclamaciones.Single();
        reclamacion.BuzonEmail.Should().Be("cae@arcosspa.example", "normalizado a minúsculas igual que en runtime");
        reclamacion.TenantPropietarioId.Should().Be(tenantM365);
        reclamacion.ConexionIntegracionId.Should().Be(conexionM365Id);
    }

    /// <summary>
    /// Simula el propio bug que este incremento cierra: dos Tenants ya
    /// comparten el mismo buzón ANTES del backfill (posible antes de este
    /// incremento). <c>ON CONFLICT DO NOTHING</c> debe resolver a favor del
    /// más antiguo, nunca lanzar ni backfillear los dos.
    /// </summary>
    [Fact]
    public async Task El_backfill_resuelve_un_buzon_ya_duplicado_a_favor_del_mas_antiguo()
    {
        var tenantAntiguo = Guid.NewGuid();
        var tenantReciente = Guid.NewGuid();
        Guid conexionAntiguaId;

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

        // CreadoEnUtc no tiene setter público (sellado por EntidadBase) — se
        // retrasa aquí por SQL directo para que el orden temporal sea
        // determinista y no dependa de la resolución del reloj entre dos
        // SaveChangesAsync consecutivos en el mismo test.
        await using (var conexion = new NpgsqlConnection(_cadenaConexion))
        {
            await conexion.OpenAsync();
            await using var comando = conexion.CreateCommand();
            comando.CommandText = """UPDATE "ConexionesIntegracion" SET "CreadoEnUtc" = "CreadoEnUtc" - INTERVAL '1 day' WHERE "Id" = @id""";
            comando.Parameters.AddWithValue("id", conexionAntiguaId);
            await comando.ExecuteNonQueryAsync();
        }

        await using (var contexto = CrearContexto(Guid.NewGuid()))
        {
            var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrador.MigrateAsync();
        }

        await using var verificacion = CrearContexto(Guid.NewGuid());
        var reclamaciones = await verificacion.ReclamacionesBuzonIntegracion.ToListAsync();

        reclamaciones.Should().ContainSingle("el índice único global no puede tener dos filas para el mismo buzón");
        reclamaciones.Single().TenantPropietarioId.Should().Be(
            tenantAntiguo, "ON CONFLICT DO NOTHING debe resolver a favor de la conexión más antigua");
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
