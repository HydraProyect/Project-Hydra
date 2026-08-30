using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Auditoría módulo 6: el "state" del flujo OAuth de Microsoft 365
/// (<see cref="SolicitudConexionMicrosoft365"/>) reemplazó un payload
/// cifrado sin tenant por una fila normal — la propiedad que de verdad cierra
/// el ataque de account-linking CSRF es que RLS/el filtro de tenant impida
/// que la sesión de la víctima (tenant B) lea una fila que el atacante
/// sembró desde el tenant A. Mismo patrón de doble contexto que
/// <see cref="AislamientoMultiTenantTests"/>.
/// </summary>
public class AislamientoSolicitudConexionMicrosoft365Tests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto(_tenantA);
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto(Guid? tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    [Fact]
    public async Task Una_solicitud_sembrada_por_el_tenant_A_es_invisible_para_el_tenant_B()
    {
        var usuarioAtacanteId = Guid.NewGuid();
        Guid solicitudId;
        await using (var contextoA = CrearContexto(_tenantA))
        {
            var solicitud = new SolicitudConexionMicrosoft365(usuarioAtacanteId, null, null, DateTime.UtcNow);
            new SolicitudConexionMicrosoft365Repository(contextoA).Agregar(solicitud);
            await contextoA.SaveChangesAsync();
            solicitudId = solicitud.Id;
        }

        await using var contextoB = CrearContexto(_tenantB);
        var repositorioB = new SolicitudConexionMicrosoft365Repository(contextoB);

        var solicitudVistaDesdeB = await repositorioB.ObtenerPorIdAsync(solicitudId, CancellationToken.None);

        solicitudVistaDesdeB.Should().BeNull();
    }
}
