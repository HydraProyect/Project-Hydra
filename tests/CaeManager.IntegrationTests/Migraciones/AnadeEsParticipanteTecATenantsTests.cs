using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// <c>Tenants.EsParticipanteTec</c> (D3) contra PostgreSQL real: la columna nace NOT NULL y con DEFAULT false, sin
/// backfill. Es lo que deja a la ranura anterior de un relevo azul/verde insertar Tenants sin conocerla, y lo que
/// garantiza que ningún Tenant existente queda marcado como participante TEC al migrar.
/// </summary>
public class AnadeEsParticipanteTecATenantsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }

    [Fact]
    public async Task La_columna_nace_no_nula_y_en_falso_por_defecto()
    {
        await using var contexto = CrearContexto();

        var columna = await contexto.Database
            .SqlQueryRaw<string>(
                """
                SELECT is_nullable || '|' || COALESCE(column_default, '') AS "Value" FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'Tenants' AND column_name = 'EsParticipanteTec'
                """)
            .SingleAsync();

        columna.Should().Be("NO|false");
    }
}
