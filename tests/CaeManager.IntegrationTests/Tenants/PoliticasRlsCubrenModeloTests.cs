using CaeManager.Domain.Common;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// P1-3 de la revisión de madurez: <c>HabilitarRlsPostgres</c> (y las
/// migraciones que la ampliaron después — <c>HabilitarRlsClavesApi</c>,
/// <c>HabilitarRlsIntegraciones</c>) graban una lista de nombres de tabla
/// fija en el momento en que se escribieron, no una introspección del modelo
/// en cada arranque. Dos veces ya una tabla nueva con <c>TenantId</c> se ha
/// quedado sin RLS simplemente porque nadie recordó añadir su propia
/// migración (<c>ClavesApi</c> de P3-29, y las 4 tablas de Integraciones de
/// P3-33) — el filtro global de EF (reflexión, primera línea) las protegía
/// igualmente, pero la segunda línea quedaba inerte para ellas sin que nada
/// lo hiciera evidente.
///
/// Este test compara el catálogo real de PostgreSQL (<c>pg_policies</c>)
/// contra el modelo de EF vigente (toda entidad asignable a
/// <see cref="EntidadConTenant"/>) para que ese hueco falle en CI la próxima
/// vez que pase, en vez de esperar a que alguien lo note por revisión manual.
/// </summary>
public class PoliticasRlsCubrenModeloTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = Guid.NewGuid() };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        await using var dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Toda_entidad_con_TenantId_del_modelo_tiene_su_politica_aislamiento_tenant()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = Guid.NewGuid() };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;
        await using var dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);

        var tablasDelModelo = dbContext.Model.GetEntityTypes()
            .Where(t => typeof(EntidadConTenant).IsAssignableFrom(t.ClrType))
            .Select(t => t.GetTableName())
            .Where(nombre => nombre is not null)
            .ToHashSet();

        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = "SELECT tablename FROM pg_policies WHERE policyname = 'aislamiento_tenant';";

        var tablasConPolitica = new HashSet<string>();
        await using (var lector = await consulta.ExecuteReaderAsync())
        {
            while (await lector.ReadAsync())
                tablasConPolitica.Add(lector.GetString(0));
        }

        var sinPolitica = tablasDelModelo.Except(tablasConPolitica!).ToList();

        sinPolitica.Should().BeEmpty(
            "toda entidad que extiende EntidadConTenant debe tener una política RLS " +
            "'aislamiento_tenant' — si esto falla, falta una migración " +
            "'HabilitarRlsX' para la(s) tabla(s) listada(s) (ver HabilitarRlsClavesApi/" +
            "HabilitarRlsIntegraciones como plantilla, y RUNBOOK-RLS.md)");
    }
}
