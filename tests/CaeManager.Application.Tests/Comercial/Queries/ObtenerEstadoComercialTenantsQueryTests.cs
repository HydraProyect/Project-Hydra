using CaeManager.Application.Comercial.Queries.ObtenerEstadoComercialTenants;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comercial.Queries;

public class ObtenerEstadoComercialTenantsQueryTests
{
    [Fact]
    public async Task Devuelve_lista_vacia_si_quien_pregunta_no_opera_desde_el_tenant_de_plataforma()
    {
        // Tenant no lleva filtro global por tenant (ver TenantConfiguration):
        // sin esta comprobación, cualquier Administrador de cualquier tenant
        // cliente vería el estado comercial de todos los demás.
        var cliente = new Tenant("Ibertec");
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(cliente);

        var handler = new ObtenerEstadoComercialTenantsQueryHandler(
            dbContext, new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", Guid.NewGuid()));

        var resultado = await handler.Handle(new ObtenerEstadoComercialTenantsQuery(), CancellationToken.None);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task Lista_los_tenants_cliente_pero_nunca_el_tenant_de_plataforma()
    {
        var plataforma = new Tenant("TALVEG");
        plataforma.MarcarComoPlataforma();
        var clienteA = new Tenant("Zeta SL");
        var clienteB = new Tenant("Arcos SPA");
        clienteB.VincularSuscripcionStripe("cus_1", "sub_1", EstadoComercialTenant.Activa);

        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(plataforma);
        dbContext.ListaTenants.Add(clienteA);
        dbContext.ListaTenants.Add(clienteB);

        var handler = new ObtenerEstadoComercialTenantsQueryHandler(
            dbContext, new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id));

        var resultado = await handler.Handle(new ObtenerEstadoComercialTenantsQuery(), CancellationToken.None);

        resultado.Should().HaveCount(2);
        resultado.Should().NotContain(dto => dto.TenantId == plataforma.Id);
        // Orden alfabético por nombre.
        resultado.Select(dto => dto.Nombre).Should().ContainInOrder("Arcos SPA", "Zeta SL");
        resultado.Single(dto => dto.TenantId == clienteB.Id).EstadoComercial.Should().Be(EstadoComercialTenant.Activa);
    }
}
