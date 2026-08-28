using CaeManager.Application.Common;
using CaeManager.Domain.Subcontratas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Requerimiento global nº 1 sobre ADR-005: la siembra de datos de prueba
/// debe cubrir la supervisión de subcontratas con todas sus variantes —
/// ambos niveles de servicio, los tres resultados de verificación, casos con
/// y sin caducidad declarada, y el caso de historial (dos verificaciones del
/// mismo tipo). Corre sobre una base de datos limpia porque la siembra real
/// solo actúa en tenants vírgenes (guard de "ya hay Clientes").
/// </summary>
public class DatosPruebaSupervisionSeederTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto()
    {
        // Tenant #1: el único con catálogo de TipoDocumento sembrado por
        // HasData, que la siembra de datos operativos necesita.
        var tenantActual = new TenantActualAmbiental { TenantId = TenantSeedData.IdPorDefecto };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    [Fact]
    public async Task La_siembra_cubre_todas_las_variantes_de_supervision()
    {
        await using (var dbContext = CrearContexto())
        using (AmbitoTenantExplicito.Establecer(TenantSeedData.IdPorDefecto))
        {
            var resumen = await DatosPruebaSeeder.SembrarSoloDatosAsync(dbContext, NullLogger.Instance);
            resumen.Should().NotBeNull("la base de datos está vacía, la siembra no debe omitirse");
        }

        await using var contexto = CrearContexto();

        // F3b-Subcontrata: DatosPruebaSeeder ya crea las subcontratas de
        // demo como Empresa (EsCritico null, NivelServicio informado), no en
        // la tabla legacy Subcontratas — mismo motivo que Cliente.
        var subcontratas = await contexto.Empresas.Where(e => e.NivelServicio != null).ToListAsync();
        subcontratas.Should().Contain(s => s.NivelServicio == NivelServicioSubcontrata.Gestionada.ToString());
        subcontratas.Should().Contain(s => s.NivelServicio == NivelServicioSubcontrata.Supervisada.ToString());

        var verificaciones = await contexto.VerificacionesExternaSubcontrata.ToListAsync();
        verificaciones.Should().NotBeEmpty();

        // Los tres resultados posibles.
        verificaciones.Should().Contain(v => v.Resultado == ResultadoVerificacionExterna.Valido);
        verificaciones.Should().Contain(v => v.Resultado == ResultadoVerificacionExterna.NoValido);
        verificaciones.Should().Contain(v => v.Resultado == ResultadoVerificacionExterna.NoEncontrado);

        // Con y sin caducidad declarada, incluida una ya vencida (rojo) y una vigente.
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        verificaciones.Should().Contain(v => v.ValidoHasta == null && v.Resultado == ResultadoVerificacionExterna.Valido);
        verificaciones.Should().Contain(v => v.ValidoHasta != null && v.ValidoHasta < hoy);
        verificaciones.Should().Contain(v => v.ValidoHasta != null && v.ValidoHasta > hoy);

        // Historial: al menos un (subcontrata, centro, tipo) con más de una
        // verificación — el semáforo debe salir de la última.
        verificaciones
            .GroupBy(v => new { v.SubcontrataId, v.CentroId, v.TipoDocumentoId })
            .Should().Contain(g => g.Count() > 1);

        // Una subcontrata Gestionada también lleva verificaciones: son hechos
        // registrables en ambos niveles.
        var idsGestionadas = subcontratas
            .Where(s => s.NivelServicio == NivelServicioSubcontrata.Gestionada.ToString())
            .Select(s => s.Id)
            .ToHashSet();
        verificaciones.Should().Contain(v => idsGestionadas.Contains(v.SubcontrataId));
    }
}
