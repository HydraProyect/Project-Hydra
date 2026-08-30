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
    public async Task Verifica_y_resuelve_el_tenant_propietario_cuando_el_clientState_y_el_subscriptionId_coinciden()
    {
        // Contexto de un tenant DISTINTO al propietario — simula la petición
        // HTTP anónima, sin ningún tenant de sesión resuelto todavía.
        await using var contexto = CrearContexto(Guid.NewGuid());
        var resolver = new WebhookTenantResolver(contexto);

        var resultado = await resolver.VerificarAsync(_conexionId, "secreto-correcto", "graph-sub-1", CancellationToken.None);

        resultado.Verificado.Should().BeTrue();
        resultado.TenantId.Should().Be(_tenantPropietario);
    }

    [Fact]
    public async Task Rechaza_un_clientState_incorrecto()
    {
        await using var contexto = CrearContexto(Guid.NewGuid());
        var resolver = new WebhookTenantResolver(contexto);

        var resultado = await resolver.VerificarAsync(_conexionId, "secreto-falso", "graph-sub-1", CancellationToken.None);

        resultado.Verificado.Should().BeFalse();
        resultado.TenantId.Should().BeNull();
    }

    /// <summary>Auditoría módulo 6: segunda comprobación además del clientState — reduce lo que un clientState filtrado, por sí solo, podría hacer aceptar.</summary>
    [Fact]
    public async Task Rechaza_un_subscriptionId_que_no_coincide_con_la_suscripcion_activa()
    {
        await using var contexto = CrearContexto(Guid.NewGuid());
        var resolver = new WebhookTenantResolver(contexto);

        var resultado = await resolver.VerificarAsync(_conexionId, "secreto-correcto", "graph-sub-de-otra-suscripcion", CancellationToken.None);

        resultado.Verificado.Should().BeFalse();
        resultado.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task Rechaza_un_subscriptionId_ausente()
    {
        await using var contexto = CrearContexto(Guid.NewGuid());
        var resolver = new WebhookTenantResolver(contexto);

        var resultado = await resolver.VerificarAsync(_conexionId, "secreto-correcto", null, CancellationToken.None);

        resultado.Verificado.Should().BeFalse();
    }

    /// <summary>Auditoría módulo 6: una notificación que llega después de que la suscripción caducara localmente no debe aceptarse aunque clientState y subscriptionId coincidan.</summary>
    [Fact]
    public async Task Rechaza_una_suscripcion_ya_caducada()
    {
        var conexionCaducada = new ConexionIntegracion("otro@cliente.com", "Buzón caducado");
        var suscripcionCaducada = new SuscripcionWebhook(conexionCaducada.Id, "graph-sub-2", "secreto-caducado", DateTime.UtcNow.AddMinutes(-5));

        await using (var contextoSetup = CrearContexto(_tenantPropietario))
        {
            contextoSetup.ConexionesIntegracion.Add(conexionCaducada);
            contextoSetup.SuscripcionesWebhook.Add(suscripcionCaducada);
            await contextoSetup.SaveChangesAsync();
        }

        await using var contexto = CrearContexto(Guid.NewGuid());
        var resolver = new WebhookTenantResolver(contexto);

        var resultado = await resolver.VerificarAsync(conexionCaducada.Id, "secreto-caducado", "graph-sub-2", CancellationToken.None);

        resultado.Verificado.Should().BeFalse();
        resultado.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task Rechaza_una_conexion_que_no_existe()
    {
        await using var contexto = CrearContexto(Guid.NewGuid());
        var resolver = new WebhookTenantResolver(contexto);

        var resultado = await resolver.VerificarAsync(Guid.NewGuid(), "cualquier-secreto", "graph-sub-1", CancellationToken.None);

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
