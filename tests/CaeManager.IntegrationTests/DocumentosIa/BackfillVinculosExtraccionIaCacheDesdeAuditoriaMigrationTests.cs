using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.DocumentosIa;

/// <summary>
/// Hallazgo de revisión Codex sobre REC-036/DEC-34: la migración que crea
/// <c>ExtraccionesIaCacheDocumentos</c> deja las entradas de caché ya
/// existentes sin ningún vínculo — <see cref="BackfillVinculosExtraccionIaCacheDesdeAuditoria"/>
/// las reconstruye desde <c>AuditoriasExtraccionIa</c>. Migra hasta justo
/// ANTES del backfill, siembra datos sintéticos que simulan una entrada
/// pre-existente, avanza hasta el final y comprueba que el vínculo aparece.
///
/// Siembra deliberadamente el <c>TipoEsperado</c> de la auditoría CON
/// mayúsculas y espacios distintos al de la caché — <c>AuditoriaExtraccionIa</c>
/// lo guarda tal cual llega, <c>ExtraccionIaCache</c> lo normaliza al
/// guardarlo (ver <see cref="ExtraccionIaCache.NormalizarTipoEsperado"/>) —
/// para que este test falle si la migración compara los dos valores tal
/// cual en vez de normalizar uno de los lados.
/// </summary>
public class BackfillVinculosExtraccionIaCacheDesdeAuditoriaMigrationTests : IAsyncLifetime
{
    private const string MigracionAntesDelBackfill = "HabilitarRlsExtraccionIaCacheDocumento";
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrador.MigrateAsync(MigracionAntesDelBackfill);
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_backfill_reconstruye_el_vinculo_desde_la_auditoria_normalizando_el_tipo_esperado()
    {
        Guid documentoId, cacheId;

        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Backfill Vínculo Caché IA S.L.", "B12345674", false, null, null);
            contexto.Empresas.Add(cliente);

            var tipo = new TipoDocumento(
                "Certificado backfill REC-036", 12, aplicaVencimientoAutomatico: true, 1,
                AmbitoAplicacion.Cliente, requerido: RequisitoDocumental.Si);
            contexto.TiposDocumento.Add(tipo);
            await contexto.SaveChangesAsync();

            var documento = Documento.DeCliente(cliente.Id, tipo.Id, new DateOnly(2025, 1, 1), null);
            contexto.Documentos.Add(documento);

            var cache = ExtraccionIaCache.Crear(
                new string('b', ExtraccionIaCache.LongitudHash), "  Apto   MÉDICO  ", """{"campo":"sintetico"}""");
            contexto.ExtraccionesIaCache.Add(cache);
            await contexto.SaveChangesAsync();

            documentoId = documento.Id;
            cacheId = cache.Id;

            // Simula una AuditoriaExtraccionIa escrita por el código ANTES de
            // que existiera ExtraccionIaCacheDocumento: mismo hash, mismo
            // DocumentoId, pero TipoEsperado sin normalizar (tal como lo
            // guarda RegistrarAuditoriaAsync — nunca lo normaliza).
            contexto.AuditoriasExtraccionIa.Add(AuditoriaExtraccionIa.Crear(
                cache.HashSha256, "Apto Médico", "anthropic", 1200,
                costeEstimadoOcr: null, costeEstimado: 0.02m, numeroPaginas: 1, confianzaGeneral: 95,
                incidencias: null, documentoId: documentoId));
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrador.MigrateAsync(); // hasta el final: incluye el backfill.
        }

        await using var verificacion = CrearContexto();
        var vinculo = await verificacion.ExtraccionesIaCacheDocumentos
            .SingleOrDefaultAsync(v => v.ExtraccionIaCacheId == cacheId);

        vinculo.Should().NotBeNull(
            "la auditoría ya conocía el hash, el tipo esperado (sin normalizar) y el DocumentoId — el backfill " +
            "tiene que reconstruir el vínculo, normalizando el tipo esperado igual que ExtraccionIaCache.Crear");
        vinculo!.DocumentoId.Should().Be(documentoId);
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
