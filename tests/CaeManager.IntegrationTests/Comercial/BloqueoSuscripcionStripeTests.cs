using CaeManager.Application.Comercial.Commands.ActualizarEstadoComercialTenant;
using CaeManager.Application.Comercial.Common;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests.ApiKeys;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Comercial;

/// <summary>
/// Los candados de <see cref="BloqueoSuscripcionStripe"/> en PostgreSQL de verdad, que los dobles
/// de los tests de aplicación no pueden observar: que el intento no espera, que el bloqueo de
/// varias Subscriptions va en orden ordinal (lo que evita el ciclo entre dos altas), que fuera de
/// una transacción no se toma, y que la resincronización decide con lo que hay en la base aunque
/// el contexto del circuito conserve una copia obsoleta del Tenant.
/// </summary>
public class BloqueoSuscripcionStripeTests : IAsyncLifetime
{
    private const string EspacioSuscripcion = "app.suscripcion_stripe:";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _tenant = new("Tenant A");

    public async Task InitializeAsync()
    {
        _tenant.VincularSuscripcionStripe("cus_1", "sub_a", EstadoComercialTenant.Activa);
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
        contexto.Tenants.Add(_tenant);
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_intento_no_espera_si_otra_transaccion_tiene_el_candado()
    {
        await using var duenio = CrearContexto();
        await using var otro = CrearContexto();
        await using var transaccionDuenio = await duenio.Database.BeginTransactionAsync();
        (await new BloqueoSuscripcionStripe(duenio).IntentarBloquearSuscripcionAsync("sub_a")).Should().BeTrue();

        await using (var transaccionOtro = await otro.Database.BeginTransactionAsync())
        {
            (await new BloqueoSuscripcionStripe(otro).IntentarBloquearSuscripcionAsync("sub_a")).Should().BeFalse();
            await transaccionOtro.RollbackAsync();
        }

        await transaccionDuenio.CommitAsync();

        await using var transaccionTras = await otro.Database.BeginTransactionAsync();
        (await new BloqueoSuscripcionStripe(otro).IntentarBloquearSuscripcionAsync("sub_a")).Should().BeTrue("el candado de transacción se suelta al confirmar");
    }

    [Fact]
    public async Task Fuera_de_una_transaccion_no_se_toma_ningun_candado()
    {
        await using var contexto = CrearContexto();
        var bloqueo = new BloqueoSuscripcionStripe(contexto);

        await FluentActions.Invoking(() => bloqueo.IntentarBloquearSuscripcionAsync("sub_a")).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Invoking(() => bloqueo.BloquearSuscripcionesAsync(["sub_a"])).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Invoking(() => bloqueo.BloquearTenantAsync(_tenant.Id)).Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// Con «sub_a» ocupada, bloquear [«sub_b», «sub_a»] espera en «sub_a» sin haber tomado
    /// «sub_b»: va en orden ordinal, no en el orden recibido. Si tomara «sub_b» primero, dos altas
    /// que se sustituyen mutuamente podrían esperarse en ciclo.
    /// </summary>
    [Fact]
    public async Task Bloquea_varias_suscripciones_en_orden_ordinal()
    {
        await using var duenio = CrearContexto();
        await using var alta = CrearContexto();
        await using var testigo = CrearContexto();
        await using var transaccionDuenio = await duenio.Database.BeginTransactionAsync();
        await new BloqueoSuscripcionStripe(duenio).BloquearSuscripcionesAsync(["sub_a"]);

        await using var transaccionAlta = await alta.Database.BeginTransactionAsync();
        var bloqueoAlta = new BloqueoSuscripcionStripe(alta).BloquearSuscripcionesAsync(["sub_b", "sub_a"]);

        (await EsperaElCandadoAsync(EspacioSuscripcion + "sub_a", bloqueoAlta)).Should().BeTrue("«sub_a» la tiene otra transacción");
        await using (var transaccionTestigo = await testigo.Database.BeginTransactionAsync())
        {
            (await new BloqueoSuscripcionStripe(testigo).IntentarBloquearSuscripcionAsync("sub_b"))
                .Should().BeTrue("quien espera en «sub_a» no puede haber tomado antes «sub_b»");
            await transaccionTestigo.RollbackAsync();
        }

        await transaccionDuenio.CommitAsync();
        await bloqueoAlta;
        await transaccionAlta.RollbackAsync();
    }

    /// <summary>
    /// El circuito cargó el Tenant con «sub_a»; otro lo re-vinculó a «sub_b». La resincronización
    /// desde ese circuito tiene que consultar y bloquear «sub_b», no la que recuerda su contexto.
    /// </summary>
    [Fact]
    public async Task La_resincronizacion_con_la_copia_obsoleta_consulta_la_suscripcion_vigente()
    {
        await using var circuito = CrearContexto();
        (await circuito.Tenants.SingleAsync(t => t.Id == _tenant.Id)).StripeSubscriptionId.Should().Be("sub_a");

        await using (var otraSesion = CrearContexto())
        {
            var tenant = await otraSesion.Tenants.SingleAsync(t => t.Id == _tenant.Id);
            tenant.VincularSuscripcionStripe("cus_1", "sub_b", EstadoComercialTenant.Activa);
            await otraSesion.SaveChangesAsync();
        }

        var stripe = new StripeQueReporta(EstadoSuscripcionProveedor.Cancelada);
        var resultado = await Resincronizar(circuito, stripe);

        resultado.EsExitoso.Should().BeTrue();
        stripe.Consultadas.Should().Equal("sub_b");
        await using var lectura = CrearContexto();
        (await lectura.Tenants.AsNoTracking().SingleAsync(t => t.Id == _tenant.Id)).EstadoComercial
            .Should().Be(EstadoComercialTenant.Suspendida);
    }

    /// <summary>La resincronización no consulta a Stripe mientras otra transacción tiene el candado del Tenant.</summary>
    [Fact]
    public async Task La_resincronizacion_espera_a_quien_tiene_el_candado_del_tenant()
    {
        await using var circuito = CrearContexto();
        await using var otraAlta = CrearContexto();
        await using var transaccion = await otraAlta.Database.BeginTransactionAsync();
        await new BloqueoSuscripcionStripe(otraAlta).BloquearTenantAsync(_tenant.Id);

        var stripe = new StripeQueReporta(EstadoSuscripcionProveedor.Activa);
        var resincronizacion = Resincronizar(circuito, stripe);

        (await EsperaElCandadoAsync("app.estado_comercial_tenant:" + _tenant.Id, resincronizacion)).Should().BeTrue();
        stripe.Consultadas.Should().BeEmpty();

        await transaccion.CommitAsync();

        (await resincronizacion).EsExitoso.Should().BeTrue();
        stripe.Consultadas.Should().Equal("sub_a");
    }

    private Task<Result> Resincronizar(CaeManagerDbContext contexto, IPaymentProvider stripe) =>
        new ActualizarEstadoComercialTenantCommandHandler(contexto, stripe, new AutorizacionClavesApiDePrueba(_tenant.Id), new ActorFijo(),
                contexto, new TransaccionDeComando(contexto), new BloqueoSuscripcionStripe(contexto), contexto)
            .Handle(new ActualizarEstadoComercialTenantCommand(_tenant.Id), CancellationToken.None);

    /// <summary>
    /// Lo que discrimina es el catálogo y no un plazo: sin candado la operación termina sin que
    /// aparezca ninguna espera, y un plazo pasaría igual en una máquina lenta.
    /// </summary>
    private async Task<bool> EsperaElCandadoAsync(string clave, Task operacion)
    {
        await using var lectura = CrearContexto();
        for (var intento = 0; intento < 300 && !operacion.IsCompleted; intento++)
        {
            var esperando = await lectura.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value"
                FROM pg_locks l
                JOIN pg_database d ON d.oid = l.database
                WHERE l.locktype = 'advisory' AND NOT l.granted AND d.datname = current_database()
                  AND ((l.classid::bigint << 32) | l.objid::bigint) = hashtextextended({clave}, 0)
                """).SingleAsync();
            if (esperando > 0) return true;

            await Task.Delay(100);
        }

        return false;
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant.Id };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class StripeQueReporta(EstadoSuscripcionProveedor estado) : IPaymentProvider
    {
        public List<string> Consultadas { get; } = [];

        public string Codigo => "prueba";

        public Result<EventoWebhookProveedorDto> VerificarYLeerWebhook(string payloadJson, string? firma) =>
            throw new NotSupportedException();

        public Task<Result<SuscripcionProveedorDto>> ObtenerSuscripcionAsync(string idSuscripcion, CancellationToken cancellationToken = default)
        {
            lock (Consultadas)
                Consultadas.Add(idSuscripcion);
            return Task.FromResult(Result.Exito(new SuscripcionProveedorDto(idSuscripcion, "cus_1", estado)));
        }
    }

    private sealed class ActorFijo : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(null);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(null);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
        public Task<bool> SuperoTotpEnSesionAsync() => Task.FromResult(false);
    }
}
