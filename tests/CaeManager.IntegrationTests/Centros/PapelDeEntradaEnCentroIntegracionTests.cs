using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Centros;

/// <summary>
/// Papel por Centro (DECISION_LOG, 2026-11-08): el ancla <c>Centros.RelacionEmpresarialEntradaId</c> se guarda
/// y se relee en el mismo Tenant propietario, y la FK compuesta con <c>TenantId</c> rechaza en la base una
/// Relación Empresarial de otro Tenant aunque se salte el dominio. Mismo patrón que
/// <see cref="RelacionEmpresarialAciclicidadTests"/>.
/// </summary>
public class PapelDeEntradaEnCentroIntegracionTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otroTenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenant);
        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_ancla_de_una_relacion_del_mismo_Tenant_se_guarda_y_se_relee()
    {
        var (centroId, relacionId) = await SembrarCentroConRelacionAsync(_tenant);

        await using (var contexto = CrearContexto(_tenant))
        {
            var centro = await contexto.Centros.SingleAsync(c => c.Id == centroId);
            var relacion = await contexto.RelacionesEmpresariales.SingleAsync(r => r.Id == relacionId);
            centro.AnclarRelacionDeEntrada(relacion).EsExitoso.Should().BeTrue();
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto(_tenant);
        (await lectura.Centros.SingleAsync(c => c.Id == centroId)).RelacionEmpresarialEntradaId.Should().Be(relacionId);
    }

    [Fact]
    public async Task La_base_rechaza_un_ancla_hacia_una_relacion_de_otro_Tenant()
    {
        var (centroId, _) = await SembrarCentroConRelacionAsync(_tenant);
        var (_, relacionAjenaId) = await SembrarCentroConRelacionAsync(_otroTenant);

        await using var contexto = CrearContexto(_tenant);
        var actualizar = () => contexto.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "Centros" SET "RelacionEmpresarialEntradaId" = {relacionAjenaId} WHERE "Id" = {centroId}""");

        (await actualizar.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    private async Task<(Guid CentroId, Guid RelacionId)> SembrarCentroConRelacionAsync(Guid tenant)
    {
        await using var contexto = CrearContexto(tenant);
        var sufijo = Guid.NewGuid().ToString("N");
        var titular = new Empresa($"Titular {sufijo}");
        var empresaDelCentro = new Empresa($"Empresa del Centro {sufijo}");
        var contratistaPrincipal = new Empresa($"Contratista principal {sufijo}");
        contexto.Empresas.AddRange(titular, empresaDelCentro, contratistaPrincipal);
        await contexto.SaveChangesAsync();

        var relacion = RelacionEmpresarial.Crear(empresaDelCentro.Id, contratistaPrincipal.Id, DateTime.UtcNow);
        var centro = new Centro(titular.Id, empresaDelCentro.Id, $"Centro {sufijo}");
        contexto.RelacionesEmpresariales.Add(relacion);
        contexto.Centros.Add(centro);
        await contexto.SaveChangesAsync();
        return (centro.Id, relacion.Id);
    }

    private CaeManagerDbContext CrearContexto(Guid tenant)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
