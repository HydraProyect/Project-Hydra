using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Commands.ReactivarTenant;
using CaeManager.Application.Tenants.Commands.SuspenderTenant;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests.ApiKeys;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// En Blazor Server cada circuito conserva su contexto, y con él el Tenant como lo cargó. Suspender y
/// reactivar tienen que decidir con lo que hay en la base, nunca con esa copia: si no, un circuito daría
/// por suspendido un Tenant que otro ya reactivó.
/// </summary>
public class TransicionEstadoOperativoTenantTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _tenant = new("Tenant A");

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
        contexto.Tenants.Add(_tenant);
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Un_circuito_con_la_copia_obsoleta_suspende_de_verdad()
    {
        await using var circuito = CrearContexto();
        await using var otroCircuito = CrearContexto();

        (await Suspender(circuito)).EsExitoso.Should().BeTrue();
        (await Reactivar(otroCircuito)).EsExitoso.Should().BeTrue();

        (await Suspender(circuito)).EsExitoso.Should().BeTrue();

        (await EstadoEnBaseAsync()).Should().Be(EstadoTenant.Suspendido);
    }

    [Fact]
    public async Task Un_circuito_con_la_copia_obsoleta_reactiva_de_verdad()
    {
        await using var circuito = CrearContexto();
        await using var otroCircuito = CrearContexto();
        (await Suspender(circuito)).EsExitoso.Should().BeTrue();
        (await Reactivar(circuito)).EsExitoso.Should().BeTrue();
        (await Suspender(otroCircuito)).EsExitoso.Should().BeTrue();

        (await Reactivar(circuito)).EsExitoso.Should().BeTrue();

        (await EstadoEnBaseAsync()).Should().Be(EstadoTenant.Activo);
    }

    [Fact]
    public async Task La_transicion_espera_a_quien_tiene_el_candado_de_ese_Tenant()
    {
        await using var otraTransicion = CrearContexto();
        await using var transaccion = await otraTransicion.Database.BeginTransactionAsync();
        await new BloqueoEstadoOperativoTenant(otraTransicion).BloquearAsync(_tenant.Id);
        await using var circuito = CrearContexto();

        var suspension = Suspender(circuito);
        var enEspera = EsperarCandadoSinConcederAsync();

        (await Task.WhenAny(suspension, enEspera)).Should().BeSameAs(enEspera,
            "la transición no puede decidir mientras otra tiene el candado");
        (await enEspera).Should().BeTrue("PostgreSQL tiene que ver la transición esperando el candado");
        (await EstadoEnBaseAsync()).Should().Be(EstadoTenant.Activo);

        await transaccion.CommitAsync();

        (await suspension).EsExitoso.Should().BeTrue();
        (await EstadoEnBaseAsync()).Should().Be(EstadoTenant.Suspendido);
    }

    /// <summary>
    /// Lo que discrimina es el catálogo y no un plazo: sin candado la transición termina sin que
    /// aparezca ninguna espera, y un plazo pasaría igual en una máquina lenta.
    /// </summary>
    private async Task<bool> EsperarCandadoSinConcederAsync()
    {
        await using var lectura = CrearContexto();
        for (var intento = 0; intento < 300; intento++)
        {
            var esperando = await lectura.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value"
                FROM pg_locks l
                JOIN pg_database d ON d.oid = l.database
                WHERE l.locktype = 'advisory' AND NOT l.granted AND d.datname = current_database()
                """).SingleAsync();
            if (esperando > 0) return true;

            await Task.Delay(100);
        }

        return false;
    }

    private async Task<EstadoTenant> EstadoEnBaseAsync()
    {
        await using var lectura = CrearContexto();
        return await lectura.Tenants.AsNoTracking().Where(t => t.Id == _tenant.Id).Select(t => t.Estado).SingleAsync();
    }

    private Task<Domain.Common.Result> Suspender(CaeManagerDbContext contexto) =>
        new SuspenderTenantCommandHandler(contexto, new AutorizacionClavesApiDePrueba(_tenant.Id), new ActorFijo(),
                new TransaccionDeComando(contexto), new BloqueoEstadoOperativoTenant(contexto), contexto, contexto)
            .Handle(new SuspenderTenantCommand(_tenant.Id), CancellationToken.None);

    private Task<Domain.Common.Result> Reactivar(CaeManagerDbContext contexto) =>
        new ReactivarTenantCommandHandler(contexto, new AutorizacionClavesApiDePrueba(_tenant.Id), new ActorFijo(),
                new TransaccionDeComando(contexto), new BloqueoEstadoOperativoTenant(contexto), contexto, contexto)
            .Handle(new ReactivarTenantCommand(_tenant.Id), CancellationToken.None);

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant.Id };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
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
