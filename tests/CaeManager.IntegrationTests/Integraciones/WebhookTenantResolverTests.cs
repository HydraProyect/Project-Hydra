using CaeManager.Application.Common;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Integraciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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
        // Filtro de EF de un tenant DISTINTO al propietario, sobre una conexión propietaria sin contexto firmado:
        // comprueba que el resolver no depende de ese filtro. La petición anónima real, firmada y sin Tenant,
        // se mide con CrearContextoRuntime; con un Tenant firmado la función no resuelve.
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

    /// <summary>
    /// El índice único de <c>SuscripcionesWebhook</c> es (TenantId, ConexionIntegracionId) y no hay clave foránea
    /// compuesta hacia la conexión, así que la base admite la misma conexión con suscripciones en dos Tenants.
    /// <c>app_tenant_de_webhook_graph</c> devuelve NULL en ese caso en vez de elegir uno por el orden de la
    /// consulta: ni el secreto del primero ni el del segundo verifican.
    /// </summary>
    [Fact]
    public async Task Graph_bajo_runtime_rechaza_una_conexion_con_suscripciones_en_dos_Tenants()
    {
        await using (var otroContexto = CrearContexto(_otroTenant))
        {
            otroContexto.SuscripcionesWebhook.Add(
                new SuscripcionWebhook(_conexionId, "graph-sub-duplicada", "secreto-duplicado", DateTime.UtcNow.AddDays(3)));
            await otroContexto.SaveChangesAsync();
        }

        await using var contexto = CrearContextoRuntime();
        var graph = new WebhookTenantResolver(contexto);

        (await graph.VerificarAsync(_conexionId, "secreto-correcto", "graph-sub-1", default)).TenantId.Should().BeNull();
        (await graph.VerificarAsync(_conexionId, "secreto-duplicado", "graph-sub-duplicada", default)).TenantId.Should().BeNull();
        (await graph.VerificarAsync(_otraConexionId, "otro-secreto", "graph-sub-otro", default)).TenantId.Should().Be(_otroTenant);
    }

    /// <summary>
    /// Contrato de catálogo de las dos funciones definidoras, el mismo que <c>app_tenant_de_clave_api</c>: si se
    /// pierde el <c>search_path</c> fijado o el <c>REVOKE ... FROM PUBLIC</c>, los tests de comportamiento seguirían
    /// en verde y cualquier rol con conexión tendría un oráculo de identificador a TenantId.
    /// </summary>
    [Fact]
    public async Task Funciones_de_resolucion_de_webhook_tienen_el_contrato_de_catalogo_de_las_definidoras()
    {
        await using var propietario = new NpgsqlConnection(_cadenaConexion);
        await propietario.OpenAsync();
        await using var orden = new NpgsqlCommand(
            """
            SELECT p.proname,
                   p.prosecdef,
                   pg_get_userbyid(p.proowner),
                   array_to_string(p.proconfig, ';'),
                   pg_get_function_identity_arguments(p.oid),
                   has_function_privilege('cae_app_runtime', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_soporte', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_aprovisionamiento', p.oid, 'EXECUTE'),
                   p.proacl IS NULL OR EXISTS (SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee = 0),
                   p.provolatile
            FROM pg_proc p
            WHERE p.proname LIKE 'app\_tenant\_de\_webhook\_%' AND p.pronamespace = 'public'::regnamespace
            ORDER BY p.proname;
            """, propietario);
        var funciones = new Dictionary<string, (bool Definer, string Propietario, string Config, string Argumentos, bool Runtime, bool Soporte, bool Aprovisionamiento, bool Public, char Volatilidad)>();
        await using (var lector = await orden.ExecuteReaderAsync())
        {
            while (await lector.ReadAsync())
                funciones[lector.GetString(0)] = (lector.GetBoolean(1), lector.GetString(2), lector.GetString(3), lector.GetString(4),
                    lector.GetBoolean(5), lector.GetBoolean(6), lector.GetBoolean(7), lector.GetBoolean(8), lector.GetChar(9));
        }

        var esperadas = new Dictionary<string, string>
        {
            ["app_tenant_de_webhook_graph"] = "p_conexion uuid",
            ["app_tenant_de_webhook_whatsapp"] = "p_phone_number_id text",
        };
        funciones.Keys.Should().BeEquivalentTo(esperadas.Keys);

        foreach (var (nombre, argumentos) in esperadas)
        {
            var f = funciones[nombre];
            f.Definer.Should().BeTrue($"{nombre}: SECURITY DEFINER");
            f.Propietario.Should().NotBe("cae_app_runtime");
            f.Config.Should().Be("search_path=pg_catalog, pg_temp", nombre);
            f.Argumentos.Should().Be(argumentos, nombre);
            f.Runtime.Should().BeTrue(nombre);
            f.Soporte.Should().BeFalse(nombre);
            f.Aprovisionamiento.Should().BeFalse(nombre);
            f.Public.Should().BeFalse($"{nombre}: REVOKE ALL ... FROM PUBLIC");
            f.Volatilidad.Should().Be('s', $"{nombre}: STABLE, solo lee");
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
