using CaeManager.Domain.Empresas;
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

namespace CaeManager.IntegrationTests.Trabajadores;

/// <summary>
/// Migración <c>AcotarDniUnicoTrasAnonimizar</c> — auditoría Módulo 5,
/// hallazgo crítico 9/9. Se siembra ANTES de aplicar la migración objetivo
/// (contra el esquema tal como quedó justo antes, con Dni NOT NULL y el
/// índice único sin filtro) para reproducir el escenario real de un
/// trabajador ya anonimizado con la fila heredada <c>Dni = ''</c>.
/// </summary>
public class AcotarDniUnicoTrasAnonimizarMigrationTests : IAsyncLifetime
{
    private const string MigracionAntes = "AcotarActivoUnicoProyectoTecnico";
    private const string MigracionObjetivo = "AcotarDniUnicoTrasAnonimizar";

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
    public async Task Convierte_el_dni_vacio_heredado_de_un_anonimizado_a_null()
    {
        // El dominio YA corregido nunca vuelve a escribir Dni=''
        // (Trabajador.Anonimizar deja null) — para reproducir la fila
        // heredada de ANTES del fix hay que forzarla con SQL crudo, no con
        // el método de dominio.
        Guid trabajadorAnonimizadoId, trabajadorConDniId;

        await using (var contexto = CrearContexto())
        {
            var empresa = new Empresa("Empresa Migración Dni S.L.", "B10380186");
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var anonimizado = Trabajador.DeEmpresa(empresa.Id, "Ana", "García", "77189989B");
            var conDni = Trabajador.DeEmpresa(empresa.Id, "Luis", "Pérez", "12345678Z");
            contexto.Trabajadores.AddRange(anonimizado, conDni);
            await contexto.SaveChangesAsync();

            trabajadorAnonimizadoId = anonimizado.Id;
            trabajadorConDniId = conDni.Id;

            await contexto.Database.ExecuteSqlRawAsync(
                """UPDATE "Trabajadores" SET "Dni" = '', "AnonimizadoEnUtc" = now() WHERE "Id" = {0}""",
                trabajadorAnonimizadoId);
        }

        await using (var contexto = CrearContexto())
        {
            var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();

            // No debe fallar: si la migración intentara crear el índice
            // filtrado sin antes convertir el '' heredado, no chocaría por sí
            // sola (el filtro ya excluye ''… salvo que "" siga contando como
            // NOT NULL), así que lo que este test prueba de verdad es la
            // conversión de datos, comprobada abajo.
            await migrador.MigrateAsync(MigracionObjetivo);
        }

        await using var verificacion = CrearContexto();
        var anonimizadoDespues = await verificacion.Trabajadores.SingleAsync(t => t.Id == trabajadorAnonimizadoId);
        var conDniDespues = await verificacion.Trabajadores.SingleAsync(t => t.Id == trabajadorConDniId);

        anonimizadoDespues.Dni.Should().BeNull("la migración convierte el '' heredado a null");
        conDniDespues.Dni.Should().Be("12345678Z", "un trabajador no anonimizado conserva su DNI real");
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
