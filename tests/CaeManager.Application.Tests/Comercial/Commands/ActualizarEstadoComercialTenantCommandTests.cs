using CaeManager.Application.Comercial.Commands.ActualizarEstadoComercialTenant;
using CaeManager.Application.Comercial.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comercial.Commands;

public class ActualizarEstadoComercialTenantCommandTests
{
    private static Tenant CrearTenantPlataforma()
    {
        var tenant = new Tenant("TALVEG");
        tenant.MarcarComoPlataforma();
        return tenant;
    }

    [Fact]
    public async Task Bloquea_si_el_tenant_todavia_no_tiene_ninguna_suscripcion_vinculada()
    {
        var plataforma = CrearTenantPlataforma();
        var cliente = new Tenant("Ibertec"); // sin VincularSuscripcionStripe
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(plataforma);
        dbContext.ListaTenants.Add(cliente);

        var handler = new ActualizarEstadoComercialTenantCommandHandler(
            dbContext, new PaymentProviderFalso(), new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new ActualizarEstadoComercialTenantCommand(cliente.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Comercial.SinSuscripcionVinculada");
    }

    [Fact]
    public async Task Resincroniza_el_estado_comercial_con_lo_que_reporta_Stripe_ahora_mismo()
    {
        var plataforma = CrearTenantPlataforma();
        var cliente = new Tenant("Ibertec");
        cliente.VincularSuscripcionStripe("cus_999", "sub_123", EstadoComercialTenant.Activa);
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(plataforma);
        dbContext.ListaTenants.Add(cliente);

        // La suscripción pasó a impagada desde el alta — la resincronización
        // manual (botón "Actualizar desde Stripe") debe reflejarlo.
        var paymentProvider = new PaymentProviderFalso(
            CaeManager.Domain.Common.Result.Exito(new SuscripcionProveedorDto("sub_123", "cus_999", EstadoSuscripcionProveedor.Cancelada)));
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new ActualizarEstadoComercialTenantCommandHandler(
            dbContext, paymentProvider, new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id), unitOfWork);

        var resultado = await handler.Handle(new ActualizarEstadoComercialTenantCommand(cliente.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        cliente.EstadoComercial.Should().Be(EstadoComercialTenant.Suspendida);
        paymentProvider.UltimoIdConsultado.Should().Be("sub_123");
        unitOfWork.VecesGuardado.Should().Be(1);
    }
}
