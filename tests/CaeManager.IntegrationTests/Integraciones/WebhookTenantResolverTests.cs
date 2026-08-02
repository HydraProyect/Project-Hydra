using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Integraciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// P3-33: el endpoint de webhook de Microsoft 365 llega sin ninguna sesión —
/// resuelve el tenant a partir del Id de <see cref="ConexionIntegracion"/> en
/// la URL más el <c>clientState</c> (docs/MULTITENANCY.md § 8, tercer modo).
/// <see cref="WebhookTenantResolver"/> usa <c>IgnoreQueryFilters()</c> — justo
/// lo que hay que probar contra Postgres real: que resuelve el tenant
/// correcto de una <see cref="SuscripcionWebhook"/> ajena al tenant que
/// tendría activo el filtro global si estuviera puesto.
/// </summary>
public class WebhookTenantResolverTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly IDataProtectionProvider _dataProtectionProvider = new EphemeralDataProtectionProvider();
    private Guid _conexionId;
    private Guid _tenantPropietario;

    public async Task InitializeAsync()
    {
        _tenantPropietario = Guid.NewGuid();
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        _conexionId = conexion.Id;
        var suscripcion = new SuscripcionWebhook(conexion.Id, "graph-sub-1", "secreto-correcto", DateTime.UtcNow.AddDays(3));

        await using var contexto = CrearContexto(_tenantPropietario);
        await contexto.Database.MigrateAsync();
        contexto.ConexionesIntegracion.Add(conexion);
        contexto.SuscripcionesWebhook.Add(suscripcion);
        await contexto.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Verifica_y_resuelve_el_tenant_propietario_cuando_el_clientState_coincide()
    {
        // Contexto de un tenant DISTINTO al propietario — simula la petición
        // HTTP anónima, sin ningún tenant de sesión resuelto todavía.
        await using var contexto = CrearContexto(Guid.NewGuid());
        var resolver = new WebhookTenantResolver(contexto);

        var resultado = await resolver.VerificarAsync(_conexionId, "secreto-correcto", CancellationToken.None);

        resultado.Verificado.Should().BeTrue();
        resultado.TenantId.Should().Be(_tenantPropietario);
    }

    [Fact]
    public async Task Rechaza_un_clientState_incorrecto()
    {
        await using var contexto = CrearContexto(Guid.NewGuid());
        var resolver = new WebhookTenantResolver(contexto);

        var resultado = await resolver.VerificarAsync(_conexionId, "secreto-falso", CancellationToken.None);

        resultado.Verificado.Should().BeFalse();
        resultado.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task Rechaza_una_conexion_que_no_existe()
    {
        await using var contexto = CrearContexto(Guid.NewGuid());
        var resolver = new WebhookTenantResolver(contexto);

        var resultado = await resolver.VerificarAsync(Guid.NewGuid(), "cualquier-secreto", CancellationToken.None);

        resultado.Verificado.Should().BeFalse();
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual), new ConcurrenciaOptimistaInterceptor())
            .Options;

        return new CaeManagerDbContext(options, _dataProtectionProvider, tenantActual);
    }
}
