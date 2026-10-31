using CaeManager.Application.ApiKeys;
using CaeManager.Application.ApiKeys.Commands.RevocarClaveApiDeMiOrganizacion;
using CaeManager.Application.ApiKeys.Queries.ObtenerClavesApiDeMiOrganizacion;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Tenants;
using CaeManager.Domain.ApiKeys;
using FluentAssertions;

namespace CaeManager.Application.Tests.ApiKeys;

/// <summary>
/// Solo el Administrador del Tenant propietario, leído en la base, ve y revoca las claves del Tenant
/// actual (OWASP ASVS 4.0.3 V4.1.1 y V4.1.3). El listado con filas y el Tenant del ámbito los mide
/// <c>GestionClavesApiTests</c> contra PostgreSQL.
/// </summary>
public class ClavesApiDeMiOrganizacionTests
{
    [Theory]
    [InlineData("sin Tenant actual")]
    [InlineData("sin usuario")]
    [InlineData("no es Administrador del Tenant actual")]
    public async Task Sin_autoridad_no_lista_ni_revoca(string caso)
    {
        var tenant = Guid.NewGuid();
        var clave = ClaveDePrueba();
        var guardado = new UnitOfWorkFalso();
        var usuario = new CurrentUserServiceFalso(caso == "sin usuario" ? null : Guid.NewGuid());
        var tenantActual = new TenantActualFijo(caso == "sin Tenant actual" ? null : tenant);
        var administrador = new AdministradorFalso(caso == "no es Administrador del Tenant actual" ? Guid.NewGuid() : tenant);

        var claves = await new ObtenerClavesApiDeMiOrganizacionQueryHandler(
                new ContextoQueNoSeLee(), new RepositorioClaves(clave), tenantActual, usuario, administrador)
            .Handle(new ObtenerClavesApiDeMiOrganizacionQuery(), CancellationToken.None);
        var resultado = await new RevocarClaveApiDeMiOrganizacionCommandHandler(
                new RepositorioClaves(clave), tenantActual, usuario, administrador, guardado)
            .Handle(new RevocarClaveApiDeMiOrganizacionCommand(clave.Id), CancellationToken.None);

        claves.Should().BeEmpty();
        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(AutoridadClavesApiDelTenant.SoloAdministradorDelTenant.Codigo);
        clave.EstaActiva.Should().BeTrue();
        guardado.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task El_Administrador_revoca_una_clave_del_Tenant_actual()
    {
        var tenant = Guid.NewGuid();
        var clave = ClaveDePrueba();
        var guardado = new UnitOfWorkFalso();

        var resultado = await new RevocarClaveApiDeMiOrganizacionCommandHandler(
                new RepositorioClaves(clave), new TenantActualFijo(tenant), new CurrentUserServiceFalso(Guid.NewGuid()), new AdministradorFalso(tenant), guardado)
            .Handle(new RevocarClaveApiDeMiOrganizacionCommand(clave.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        clave.EstaActiva.Should().BeFalse();
        guardado.VecesGuardado.Should().Be(1);
    }

    // El filtro global esconde la clave de otro Tenant: para este comando no existe.
    [Fact]
    public async Task Una_clave_que_el_Tenant_actual_no_ve_no_se_revoca()
    {
        var tenant = Guid.NewGuid();
        var clave = ClaveDePrueba();
        var guardado = new UnitOfWorkFalso();

        var resultado = await new RevocarClaveApiDeMiOrganizacionCommandHandler(
                new RepositorioClaves(clave), new TenantActualFijo(tenant), new CurrentUserServiceFalso(Guid.NewGuid()), new AdministradorFalso(tenant), guardado)
            .Handle(new RevocarClaveApiDeMiOrganizacionCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ClaveApi.NoEncontrada");
        clave.EstaActiva.Should().BeTrue();
        guardado.VecesGuardado.Should().Be(0);
    }

    private static ClaveApi ClaveDePrueba() =>
        new("Integración Alfa", "hydra_prueba", "hash", Guid.NewGuid(), DateTime.UtcNow.AddDays(30), "Prueba");

    private sealed class AdministradorFalso(Guid tenantDelAdministrador) : IAdministradorDelTenantPropietario
    {
        public Task<bool> EsAdministradorEnBaseAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenantDelAdministrador == tenantId);
    }

    private sealed class TenantActualFijo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class ContextoQueNoSeLee : IApiKeysQueryContext
    {
        public IQueryable<ClaveApi> ClavesApi => throw new InvalidOperationException("Sin autoridad no se leen las claves.");
    }

    private sealed class RepositorioClaves(ClaveApi clave) : IClaveApiRepository
    {
        public Task<ClaveApi?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<ClaveApi?>(id == clave.Id ? clave : null);
        public void Agregar(ClaveApi clave) => throw new NotSupportedException();
        public Task<ClaveApi?> ObtenerPorHashAsync(string hashClave, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> ObtenerTenantPorHashAsync(string hashClave, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ClaveSigueRespaldadaAsync(Guid tenantPropietarioId, Guid creadaPorUsuarioId, DateTime emitidaEnUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
