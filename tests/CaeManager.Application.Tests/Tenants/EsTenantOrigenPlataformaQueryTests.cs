using CaeManager.Application.Tenants.Queries.EsTenantOrigenPlataforma;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// Hueco declarado en PR #651: Delegaciones.razor solo ocultaba "Abrir
/// acceso"/"Cerrar acceso" con !OperandoWorkspaceAjeno, que no comprueba la
/// mitad real del criterio de AbrirAccesoSoporteCommand/CerrarAccesoSoporteCommand
/// que exige <c>Tenant.EsPlataforma</c> del tenant de ORIGEN. Estos tests
/// prueban <see cref="EsTenantOrigenPlataformaQueryHandler"/>, que delega en el
/// mismo helper (<c>AutorizacionAccesoSoporte</c>) que usan esos dos comandos —
/// no una copia de su consulta.
/// </summary>
public class EsTenantOrigenPlataformaQueryTests
{
    [Fact]
    public async Task El_tenant_de_origen_marcado_como_plataforma_autoriza()
    {
        var tenantPlataforma = new Tenant("TALVEG");
        tenantPlataforma.MarcarComoPlataforma();
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(tenantPlataforma);
        var handler = new EsTenantOrigenPlataformaQueryHandler(
            dbContext, new CurrentUserServiceFalso(tenantOrigenId: tenantPlataforma.Id));

        var resultado = await handler.Handle(new EsTenantOrigenPlataformaQuery(), CancellationToken.None);

        resultado.Should().BeTrue();
    }

    [Fact]
    public async Task Un_tenant_de_origen_sin_marcar_no_autoriza()
    {
        var tenantCliente = new Tenant("Organización Norte");
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(tenantCliente);
        var handler = new EsTenantOrigenPlataformaQueryHandler(
            dbContext, new CurrentUserServiceFalso(tenantOrigenId: tenantCliente.Id));

        var resultado = await handler.Handle(new EsTenantOrigenPlataformaQuery(), CancellationToken.None);

        resultado.Should().BeFalse(
            "control negativo: mismo predicado que niega el acceso a AbrirAccesoSoporteCommand para " +
            "cualquier tenant de origen que no sea la organización TALVEG");
    }

    [Fact]
    public async Task Sin_tenant_de_origen_no_autoriza()
    {
        var dbContext = new TenantsQueryContextFalso();
        var handler = new EsTenantOrigenPlataformaQueryHandler(dbContext, new CurrentUserServiceFalso());

        var resultado = await handler.Handle(new EsTenantOrigenPlataformaQuery(), CancellationToken.None);

        resultado.Should().BeFalse("fallo cerrado: fuera de un circuito autenticado no hay tenant de origen");
    }
}
