using CaeManager.Application.Common;
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
/// la URL más el <c>clientState</c> (Project-Hydra-Negocio/tecnico/docs/MULTITENANCY.md § 8, tercer modo).
/// Las pruebas bajo el rol de tráfico comprueban la resolución y RLS efectiva.
/// </summary>
public class WebhookTenantResolverTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly IDataProtectionProvider _dataProtectionProvider = new EphemeralDataProtectionProvider();
    private Guid _conexionId;
    private Guid _tenantPropietario;
    private Guid _otroTenant;
    private Guid _otraConexionId;
    private Guid _conexionWhatsAppId;
    private Guid _otraConexionWhatsAppId;

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

        _conexionWhatsAppId = await SembrarLineaAsync(contexto, "phone-uno");
        _otroTenant = Guid.NewGuid();
        await using var otroContexto = CrearContexto(_otroTenant);
        var otraConexion = new ConexionIntegracion("otro@ejemplo.test", "Otro buzón");
        _otraConexionId = otraConexion.Id;
        otroContexto.ConexionesIntegracion.Add(otraConexion);
        otroContexto.SuscripcionesWebhook.Add(new SuscripcionWebhook(otraConexion.Id, "graph-sub-otro", "otro-secreto", DateTime.UtcNow.AddDays(3)));
        await otroContexto.SaveChangesAsync();
        _otraConexionWhatsAppId = await SembrarLineaAsync(otroContexto, "phone-dos");
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

    [Fact]
    public async Task Resolutores_bajo_runtime_resuelven_solo_la_linea_o_suscripcion_identificada()
    {
        await using var contexto = CrearContextoRuntime();
        var graph = new WebhookTenantResolver(contexto);
        var whatsapp = new WebhookWhatsAppTenantResolver(contexto);
        (await contexto.Database.SqlQuery<string>($"SELECT current_user AS \"Value\"").SingleAsync()).Should().Be("cae_app_runtime");

        (await graph.VerificarAsync(_conexionId, "secreto-correcto", "graph-sub-1", default)).TenantId.Should().Be(_tenantPropietario);
        (await graph.VerificarAsync(_otraConexionId, "otro-secreto", "graph-sub-otro", default)).TenantId.Should().Be(_otroTenant);
        (await graph.VerificarAsync(_conexionId, "otro-secreto", "graph-sub-otro", default)).TenantId.Should().BeNull();
        (await graph.VerificarAsync(Guid.NewGuid(), "secreto-correcto", "graph-sub-1", default)).TenantId.Should().BeNull();

        var primera = await whatsapp.ResolverPorPhoneNumberIdAsync("phone-uno", default);
        primera.TenantId.Should().Be(_tenantPropietario);
        primera.ConexionIntegracionId.Should().Be(_conexionWhatsAppId);
        var segunda = await whatsapp.ResolverPorPhoneNumberIdAsync("phone-dos", default);
        segunda.TenantId.Should().Be(_otroTenant);
        segunda.ConexionIntegracionId.Should().Be(_otraConexionWhatsAppId);
        var desconocida = await whatsapp.ResolverPorPhoneNumberIdAsync("phone-desconocido", default);
        desconocida.TenantId.Should().BeNull();
        desconocida.ConexionIntegracionId.Should().BeNull();

        (await contexto.SuscripcionesWebhook.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await contexto.LineasWhatsApp.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        using (AmbitoTenantExplicito.Establecer(_tenantPropietario))
        {
            (await contexto.SuscripcionesWebhook.IgnoreQueryFilters().Select(s => s.ConexionIntegracionId).ToListAsync())
                .Should().Equal(_conexionId);
            (await contexto.LineasWhatsApp.IgnoreQueryFilters().Select(l => l.ConexionIntegracionId).ToListAsync())
                .Should().Equal(_conexionWhatsAppId);
        }
    }

    private static async Task<Guid> SembrarLineaAsync(CaeManagerDbContext contexto, string phoneNumberId)
    {
        var conexion = new ConexionIntegracion($"{phoneNumberId}@ejemplo.test", "Línea WhatsApp");
        contexto.ConexionesIntegracion.Add(conexion);
        contexto.LineasWhatsApp.Add(new LineaWhatsApp(conexion.Id, phoneNumberId, "waba", "+34123456789", "token", ModoAsignacionLinea.PoolInbound));
        await contexto.SaveChangesAsync();
        return conexion.Id;
    }

    private CaeManagerDbContext CrearContextoRuntime()
    {
        var tenant = new TenantPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion), n => n.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantRlsConnectionInterceptor(tenant, new SinTenantSeleccionado(),
                new CurrentUserServiceFalso(), BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, _dataProtectionProvider, tenant);
    }

    private sealed class TenantPorAmbito(Guid? predeterminado = null) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? predeterminado;
    }

    private sealed class SinTenantSeleccionado : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantPorAmbito(tenantId);
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual), new ConcurrenciaOptimistaInterceptor())
            .Options;

        return new CaeManagerDbContext(options, _dataProtectionProvider, tenantActual);
    }
}
