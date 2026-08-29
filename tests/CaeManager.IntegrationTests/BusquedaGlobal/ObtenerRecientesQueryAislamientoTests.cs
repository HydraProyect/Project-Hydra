using CaeManager.Application.BusquedaGlobal.Queries.ObtenerRecientes;
using CaeManager.Application.Common;
using CaeManager.Domain.BusquedaGlobal;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.BusquedaGlobal;

/// <summary>
/// Aislamiento multi-tenant de "Recientes" verificado a través del propio
/// <see cref="ObtenerRecientesQueryHandler"/> (no solo del DbSet crudo, que
/// ya cubre <c>AislamientoPorAgregadoTests.Aislamiento_EventoRecienteUsuario</c>)
/// — mismo <c>UsuarioId</c> en ambos tenants a propósito, para descartar que
/// el filtrado dependa solo del usuario: si algún día alguien quitara el
/// filtro global de tenant y dejara solo el de UsuarioId en la query, este
/// test seguiría fallando porque el escenario ya tiene el mismo usuario en
/// los dos tenants.
/// </summary>
public class ObtenerRecientesQueryAislamientoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenantA);
        await contexto.Database.MigrateAsync();

        contexto.EventosRecientesUsuario.Add(new EventoRecienteUsuario(
            _usuario, "Cliente", Guid.NewGuid(), "Refrielectric S.A.", "Cliente", "/clientes?q=Refrielectric"));
        await contexto.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_tenant_que_no_creo_el_evento_no_lo_ve_aunque_sea_el_mismo_usuario()
    {
        await using var contextoB = CrearContexto(_tenantB);
        var handler = new ObtenerRecientesQueryHandler(contextoB, new CurrentUserServiceFalso(_usuario));

        var resultado = await handler.Handle(new ObtenerRecientesQuery(), CancellationToken.None);

        resultado.Should().BeEmpty("un evento reciente creado bajo otro tenant nunca debe cruzar a este, aunque el usuario coincida");
    }

    [Fact]
    public async Task El_tenant_que_creo_el_evento_si_lo_ve()
    {
        await using var contextoA = CrearContexto(_tenantA);
        var handler = new ObtenerRecientesQueryHandler(contextoA, new CurrentUserServiceFalso(_usuario));

        var resultado = await handler.Handle(new ObtenerRecientesQuery(), CancellationToken.None);

        resultado.Should().ContainSingle(r => r.Titulo == "Refrielectric S.A.");
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

    private sealed class CurrentUserServiceFalso(Guid usuarioId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);

        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("Administrador");

        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);

        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
