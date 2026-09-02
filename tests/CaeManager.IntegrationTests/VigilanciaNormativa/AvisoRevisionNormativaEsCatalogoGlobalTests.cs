using CaeManager.Application.VigilanciaNormativa;
using CaeManager.Domain.VigilanciaNormativa;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.VigilanciaNormativa;

/// <summary>
/// H-3/DEC-8, capa Infrastructure/PostgreSQL. Prueba UNA propiedad, no dos:
/// que la lectura de <see cref="AvisoRevisionNormativa"/> es un catálogo
/// global sin filtrar por tenant — dos tenants distintos ven exactamente la
/// misma fila. Eso lo garantiza la ausencia de <c>HasQueryFilter</c> en
/// <c>AvisoRevisionNormativaConfiguration</c>, y aquí se comprueba contra
/// PostgreSQL real, no contra un doble en memoria.
///
/// <b>Esto NO prueba que ningún tenant pueda escribir el catálogo.</b> Esa es
/// una propiedad de AUTORIZACIÓN (quién puede invocar
/// <c>MarcarAvisoRevisionNormativaRevisadoCommand</c>), no de ESQUEMA — vive
/// en <c>MarcarAvisoRevisionNormativaRevisadoCommandTests</c>. Mezclar las
/// dos aquí sería exactamente el error que el contrato prohíbe: alcance ≠
/// autorización. La ausencia de RLS y de columna TenantId que se comprueba
/// abajo es documentación del diseño (mismo patrón que DelegacionTenant),
/// no una garantía de protección: sin política de RLS, cualquier conexión
/// autenticada de la aplicación PUEDE escribir esta tabla a nivel de
/// PostgreSQL — lo que lo impide en la práctica es que ningún camino de
/// Application distinto del comando de arriba y del sondeo del BOE expone
/// esa escritura.
/// </summary>
public class AvisoRevisionNormativaEsCatalogoGlobalTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(Guid.NewGuid());
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    [Fact]
    public async Task Un_aviso_insertado_bajo_un_tenant_es_visible_identico_desde_otro_tenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        Guid avisoId;
        await using (var contextoA = CrearContexto(tenantA))
        {
            var aviso = new AvisoRevisionNormativa(
                "BOE-A-2026-99999", new DateOnly(2026, 8, 13),
                "Real Decreto 171/2004, de coordinación de actividades empresariales.",
                "https://www.boe.es/diario_boe/txt.php?id=BOE-A-2026-99999", "RD 171/2004", DateTime.UtcNow);
            contextoA.AvisosRevisionNormativa.Add(aviso);
            await contextoA.SaveChangesAsync();
            avisoId = aviso.Id;
        }

        await using var contextoB = CrearContexto(tenantB);
        IVigilanciaNormativaQueryContext lectorDesdeB = contextoB;

        var visto = await lectorDesdeB.AvisosRevisionNormativa.SingleOrDefaultAsync(a => a.Id == avisoId);

        // No "no lanza": visible, con el mismo contenido — la propiedad es
        // que B ve la MISMA fila que escribió A, no una copia ni un vacío.
        visto.Should().NotBeNull("un catálogo global no depende de qué tenant esté fijado en la sesión que lee");
        visto!.IdentificadorBoe.Should().Be("BOE-A-2026-99999");
        visto.NormaVigilada.Should().Be("RD 171/2004");
    }

    [Fact]
    public async Task Dos_tenants_distintos_ven_el_mismo_recuento_total_del_catalogo()
    {
        await using (var contextoSeed = CrearContexto(Guid.NewGuid()))
        {
            contextoSeed.AvisosRevisionNormativa.Add(new AvisoRevisionNormativa(
                "BOE-A-2026-11111", new DateOnly(2026, 3, 1), "Ley 31/1995, de Prevención de Riesgos Laborales.",
                "https://boe.es/1", "LPRL", DateTime.UtcNow));
            contextoSeed.AvisosRevisionNormativa.Add(new AvisoRevisionNormativa(
                "BOE-A-2026-22222", new DateOnly(2026, 4, 1), "Real Decreto 39/1997, Reglamento de los Servicios de Prevención.",
                "https://boe.es/2", "RSP", DateTime.UtcNow));
            await contextoSeed.SaveChangesAsync();
        }

        await using var contextoTenantA = CrearContexto(Guid.NewGuid());
        await using var contextoTenantB = CrearContexto(Guid.NewGuid());

        var totalDesdeA = await ((IVigilanciaNormativaQueryContext)contextoTenantA).AvisosRevisionNormativa.CountAsync();
        var totalDesdeB = await ((IVigilanciaNormativaQueryContext)contextoTenantB).AvisosRevisionNormativa.CountAsync();

        totalDesdeA.Should().Be(2);
        totalDesdeB.Should().Be(totalDesdeA, "ningún tenant tiene una vista parcial de un catálogo que no es suyo");
    }

    /// <summary>
    /// Documentación del diseño verificada contra el catálogo real, no una
    /// garantía de protección (ver el comentario de clase). Confirma que la
    /// tabla sigue el patrón deliberado: sin RLS y sin columna TenantId,
    /// porque la entidad extiende <c>Entity</c> y no <c>EntidadConTenant</c>.
    /// </summary>
    [Fact]
    public async Task La_tabla_no_tiene_RLS_ni_columna_TenantId_documentando_que_es_un_catalogo_global()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await using (var comandoRls = conexion.CreateCommand())
        {
            comandoRls.CommandText =
                "SELECT relrowsecurity FROM pg_class WHERE relname = 'AvisosRevisionNormativa';";
            var relrowsecurity = (bool?)await comandoRls.ExecuteScalarAsync();

            relrowsecurity.Should().NotBeNull("la tabla AvisosRevisionNormativa debe existir tras migrar");
            relrowsecurity!.Value.Should().BeFalse(
                "es un catálogo global (mismo patrón que DelegacionTenant): RLS aquí sería una política sin " +
                "columna que comparar, no una protección");
        }

        await using var comandoColumnas = conexion.CreateCommand();
        comandoColumnas.CommandText = @"
SELECT COUNT(*) FROM information_schema.columns
WHERE table_name = 'AvisosRevisionNormativa' AND column_name = 'TenantId';";
        var columnasTenantId = (long)(await comandoColumnas.ExecuteScalarAsync())!;

        columnasTenantId.Should().Be(0,
            "sin columna TenantId no hay coordenada con la que un tenant pudiera reclamar la fila como propia");
    }
}
