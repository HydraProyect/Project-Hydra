using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// T3 (taxonomia-documental-cae-propuesta-2026-08-27.md §2bis): cada tipo del
/// catálogo semilla renombrado conserva su nombre contaminado anterior como
/// <c>TipoDocumentoAlias</c>, para que nada que buscara por el nombre viejo
/// deje de encontrar la fila. A diferencia de <c>NaturalezaDelCatalogoSemillaTests</c>
/// (que ejercita <see cref="TipoDocumentoSeedData.CrearCopiasParaTenant"/>,
/// el camino de un tenant nuevo), este test migra Postgres real y comprueba
/// el otro camino: el <c>HasData</c> de <see cref="TipoDocumentoSeedData.AliasesParaMigracion"/>
/// que siembra el tenant #1 — el único que antes de este incremento no tenía
/// ningún alias, porque el campo (PR #313) no se usaba todavía.
/// </summary>
public class AliasesDelCatalogoSemillaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _dbContext = null!;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = TenantSeedData.IdPorDefecto };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.Database.EnsureDeletedAsync();
        await _dbContext.DisposeAsync();
    }

    [Theory]
    [InlineData("Certificado de aptitud médica", "Apto médico laboral")]
    [InlineData("Entrega de EPI", "EPIS (firma)")]
    [InlineData("Documento de identidad", "DNI o NIE en vigor")]
    [InlineData("RLC", "RLC/TC1")]
    [InlineData("RLC", "TC1")]
    [InlineData("RNT", "RNT/TC2")]
    [InlineData("RNT", "TC2")]
    [InlineData("Servicio de Prevención Ajeno", "SPA")]
    [InlineData("Evaluación de Riesgos Laborales", "EVR")]
    [InlineData("Planificación de la Actividad Preventiva", "PAP")]
    [InlineData("Tarjeta de identificación fiscal", "Tarjeta CIF")]
    [InlineData("Tarjeta de identificación fiscal", "CIF")]
    public async Task El_nombre_nuevo_conserva_el_contaminado_como_alias_buscable(string nombreNuevo, string aliasEsperado)
    {
        var tipo = await _dbContext.TiposDocumento.SingleAsync(t => t.Nombre == nombreNuevo);

        var aliases = await _dbContext.TiposDocumentoAlias
            .Where(a => a.TipoDocumentoId == tipo.Id)
            .Select(a => a.Texto)
            .ToListAsync();

        aliases.Should().Contain(aliasEsperado,
            $"\"{nombreNuevo}\" reemplaza a \"{aliasEsperado}\" y nada que buscara por el nombre viejo puede dejar de encontrarlo");
    }

    /// <summary>
    /// Los tipos de patrón F (dos documentos en un tipo) y los "problema de
    /// fondo" declarados fuera de alcance de T3 no cambian de nombre — por
    /// tanto tampoco deberían adquirir un alias que nadie pidió.
    /// </summary>
    [Theory]
    [InlineData("RLC/TC1 + Recibo de pago")]
    [InlineData("Recibo de pago RLC/TC1")]
    [InlineData("Mutua")]
    public async Task Lo_que_T3_no_toca_no_adquiere_alias(string nombreSinTocar)
    {
        var tipo = await _dbContext.TiposDocumento.SingleAsync(t => t.Nombre == nombreSinTocar);

        var tieneAlias = await _dbContext.TiposDocumentoAlias.AnyAsync(a => a.TipoDocumentoId == tipo.Id);

        tieneAlias.Should().BeFalse($"\"{nombreSinTocar}\" queda fuera del alcance de T3 (patrón F o problema de fondo)");
    }
}
