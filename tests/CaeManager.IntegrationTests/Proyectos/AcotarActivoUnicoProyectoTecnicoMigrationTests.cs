using CaeManager.Domain.Empresas;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Proyectos;

/// <summary>
/// Migración <c>AcotarActivoUnicoProyectoTecnico</c> — auditoría Módulo 5,
/// hallazgo crítico 11/9. Se siembra ANTES de aplicar la migración objetivo
/// (contra el esquema tal como quedó justo antes, con el índice viejo por
/// FechaAlta) para reproducir el escenario real: bases con el defecto ya
/// presente, no datos creados después de que el índice nuevo exista.
/// </summary>
public class AcotarActivoUnicoProyectoTecnicoMigrationTests : IAsyncLifetime
{
    private const string MigracionAntes = "AcotarResponsableClienteAGlobalVigente";
    private const string MigracionObjetivo = "AcotarActivoUnicoProyectoTecnico";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrador.MigrateAsync(MigracionAntes);
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Cierra_las_altas_activas_duplicadas_conservando_la_mas_reciente_antes_de_crear_el_indice()
    {
        Guid altaAntigua, altaReciente;

        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente(
                "Cliente Duplicado Técnico S.A.", "B10380186", esCritico: false, notas: null, ejecutivoUsuarioId: null);
            var empresa = new Empresa("Contratas Duplicado S.L.", "B10380194");
            contexto.Empresas.AddRange(cliente, empresa);
            await contexto.SaveChangesAsync();

            var centro = new CaeManager.Domain.Centros.Centro(cliente.Id, empresa.Id, "Centro Duplicado");
            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ana", "García", "77189989B");
            contexto.Centros.Add(centro);
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();

            var proyecto = Proyecto.Crear(cliente.Id, centro.Id, "Ampliación Duplicada", new DateOnly(2026, 1, 1), null, null);
            contexto.Proyectos.Add(proyecto);
            await contexto.SaveChangesAsync();

            // Dos altas ACTIVAS del mismo técnico sobre el mismo proyecto,
            // con fechas de alta distintas: el índice viejo (con FechaAlta
            // en la clave) lo permite. Se siembran por SQL crudo, no por el
            // DbContext: en MigracionAntes la tabla todavía no tiene la
            // columna "Version" (la añade HuecosArquitectonicosModulo5,
            // posterior a la migración objetivo), así que un INSERT vía EF
            // con el modelo actual fallaría con "column Version does not
            // exist".
            altaAntigua = Guid.NewGuid();
            altaReciente = Guid.NewGuid();
            await contexto.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ProyectosTecnicos" ("Id", "TenantId", "ProyectoId", "TrabajadorId", "FechaAlta", "FechaBaja")
                VALUES ({altaAntigua}, {_tenantId}, {proyecto.Id}, {trabajador.Id}, {new DateOnly(2026, 1, 5)}, NULL)
                """);
            await contexto.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ProyectosTecnicos" ("Id", "TenantId", "ProyectoId", "TrabajadorId", "FechaAlta", "FechaBaja")
                VALUES ({altaReciente}, {_tenantId}, {proyecto.Id}, {trabajador.Id}, {new DateOnly(2026, 2, 1)}, NULL)
                """);
        }

        await using (var contexto = CrearContexto())
        {
            var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();

            // No debe fallar: si la migración creara el índice sin cerrar
            // antes el duplicado heredado, este MigrateAsync lanzaría 23505.
            await migrador.MigrateAsync(MigracionObjetivo);

            // Migraciones posteriores (p. ej. HuecosArquitectonicosModulo5,
            // que añade "Version" a esta misma tabla) no son objeto de este
            // test, pero hacen falta para que la lectura de verificación de
            // abajo, hecha con el DbContext y su modelo actual, encuentre
            // todas las columnas que ese modelo espera.
            await migrador.MigrateAsync();
        }

        await using var verificacion = CrearContexto();
        var antiguaDespues = await verificacion.ProyectosTecnicos.SingleAsync(pt => pt.Id == altaAntigua);
        var recienteDespues = await verificacion.ProyectosTecnicos.SingleAsync(pt => pt.Id == altaReciente);

        antiguaDespues.EstaActivo.Should().BeFalse("la migración cierra los duplicados heredados");
        recienteDespues.EstaActivo.Should().BeTrue("se conserva la vigencia más reciente");
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
