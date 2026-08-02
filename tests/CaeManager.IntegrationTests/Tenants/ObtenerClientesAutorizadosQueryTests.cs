using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Regresión encontrada en CI (auditoría de af823ba/840eff7/1f53b53,
/// confirmada por FlujoSoporteTests E2E): un operador con dos
/// <c>AsignacionOperadorDelegado</c> activas hacia el mismo Cliente
/// Delegante — una Comercial (la de la siembra de demo) y una de Soporte
/// (al abrir un acceso) — obtenía dos filas idénticas del mismo tenant. El
/// join original no deduplicaba, así que <c>SelectorClienteActivo.razor</c>
/// pintaba dos &lt;option&gt; iguales y Playwright reventaba con "strict
/// mode violation: resolved to 2 elements" al intentar seleccionar una.
/// </summary>
public class ObtenerClientesAutorizadosQueryTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuario = Guid.NewGuid();
    private Guid _consultora;
    private Guid _clienteDelegante;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(Guid.NewGuid());
        await contexto.Database.MigrateAsync();

        var consultora = new Tenant("Consultora demo");
        var clienteDelegante = new Tenant("Cliente Delegante demo");
        contexto.Tenants.Add(consultora);
        contexto.Tenants.Add(clienteDelegante);

        _consultora = consultora.Id;
        _clienteDelegante = clienteDelegante.Id;

        // Dos delegaciones distintas hacia el mismo Cliente Delegante, las
        // dos activas, las dos con el mismo operador asignado — exactamente
        // lo que deja la siembra de demo (Comercial) más abrir un acceso de
        // soporte (Soporte) sobre el mismo tenant y el mismo Administrador.
        var comercial = new DelegacionTenant(_consultora, _clienteDelegante);
        var soporte = DelegacionTenant.ParaSoporte(_consultora, _clienteDelegante);
        soporte.ActivarParaSoporte("Incidencia de prueba", DateTime.UtcNow.AddDays(1), DateTime.UtcNow);

        contexto.DelegacionesTenant.Add(comercial);
        contexto.DelegacionesTenant.Add(soporte);
        contexto.AsignacionesOperadorDelegado.Add(new AsignacionOperadorDelegado(comercial.Id, _usuario, "Administrador"));
        contexto.AsignacionesOperadorDelegado.Add(new AsignacionOperadorDelegado(soporte.Id, _usuario, "Administrador"));

        await contexto.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Dos_delegaciones_activas_hacia_el_mismo_cliente_no_duplican_la_entrada()
    {
        await using var contexto = CrearContexto(_consultora);
        var handler = new ObtenerClientesAutorizadosQueryHandler(
            contexto, new CurrentUserServiceFalso(_usuario, _consultora));

        var resultado = await handler.Handle(new ObtenerClientesAutorizadosQuery(), CancellationToken.None);

        resultado.Should().HaveCount(2, "origen + un único Cliente Delegante, aunque haya dos delegaciones activas hacia él");
        resultado.Should().ContainSingle(c => c.TenantId == _clienteDelegante && !c.EsOrigen);
        resultado.Should().ContainSingle(c => c.TenantId == _consultora && c.EsOrigen);
    }

    private CaeManagerDbContext CrearContexto(Guid tenantSellado)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantSellado };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class CurrentUserServiceFalso(Guid usuarioId, Guid tenantOrigenId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);

        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("Administrador");

        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(tenantOrigenId);

        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
