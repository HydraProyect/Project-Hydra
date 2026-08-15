using CaeManager.Application.Comercial.Commands.RegistrarSuscripcionTenant;
using CaeManager.Application.Comercial.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comercial.Commands;

public class RegistrarSuscripcionTenantCommandTests
{
    private static Tenant CrearTenantPlataforma()
    {
        var tenant = new Tenant("TALVEG");
        tenant.MarcarComoPlataforma();
        return tenant;
    }

    [Fact]
    public async Task Bloquea_si_quien_llama_no_opera_desde_el_tenant_de_plataforma()
    {
        var cliente = new Tenant("Ibertec");
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(cliente);

        var handler = new RegistrarSuscripcionTenantCommandHandler(
            dbContext, new PaymentProviderFalso(), new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", Guid.NewGuid()), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new RegistrarSuscripcionTenantCommand(cliente.Id, "sub_123"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Comercial.NoAutorizado");
    }

    [Fact]
    public async Task Bloquea_si_el_tenant_destino_no_existe()
    {
        var plataforma = CrearTenantPlataforma();
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(plataforma);

        var handler = new RegistrarSuscripcionTenantCommandHandler(
            dbContext, new PaymentProviderFalso(), new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new RegistrarSuscripcionTenantCommand(Guid.NewGuid(), "sub_123"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Comercial.TenantNoEncontrado");
    }

    [Fact]
    public async Task Bloquea_vincular_una_suscripcion_al_propio_tenant_de_plataforma()
    {
        var plataforma = CrearTenantPlataforma();
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(plataforma);

        var handler = new RegistrarSuscripcionTenantCommandHandler(
            dbContext, new PaymentProviderFalso(), new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new RegistrarSuscripcionTenantCommand(plataforma.Id, "sub_123"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Comercial.TenantPlataforma");
    }

    [Fact]
    public async Task Propaga_el_fallo_de_Stripe_sin_vincular_nada()
    {
        var plataforma = CrearTenantPlataforma();
        var cliente = new Tenant("Ibertec");
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(plataforma);
        dbContext.ListaTenants.Add(cliente);

        var paymentProvider = new PaymentProviderFalso(
            Result.Fallo<SuscripcionProveedorDto>(Error.Crear("PaymentProvider.ErrorApi", "no encontrada")));

        var handler = new RegistrarSuscripcionTenantCommandHandler(
            dbContext, paymentProvider, new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new RegistrarSuscripcionTenantCommand(cliente.Id, "sub_inexistente"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("PaymentProvider.ErrorApi");
        cliente.StripeSubscriptionId.Should().BeNull();
    }

    [Fact]
    public async Task Vincula_la_suscripcion_con_el_estado_que_reporta_Stripe_no_uno_asumido()
    {
        var plataforma = CrearTenantPlataforma();
        var cliente = new Tenant("Ibertec");
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(plataforma);
        dbContext.ListaTenants.Add(cliente);

        // Impagada desde el primer momento: alguien pegó el Id de una
        // suscripción que ya estaba en mal estado en Stripe — el alta manual
        // no debe darla de alta como si estuviera "Activa" a ciegas.
        var paymentProvider = new PaymentProviderFalso(
            Result.Exito(new SuscripcionProveedorDto("sub_123", "cus_999", EstadoSuscripcionProveedor.Impagada)));
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new RegistrarSuscripcionTenantCommandHandler(
            dbContext, paymentProvider, new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id), unitOfWork);

        var resultado = await handler.Handle(new RegistrarSuscripcionTenantCommand(cliente.Id, "sub_123"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        cliente.StripeCustomerId.Should().Be("cus_999");
        cliente.StripeSubscriptionId.Should().Be("sub_123");
        cliente.EstadoComercial.Should().Be(EstadoComercialTenant.SoloLectura);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Bloquea_si_Stripe_devuelve_un_estado_de_suscripcion_no_reconocido()
    {
        var plataforma = CrearTenantPlataforma();
        var cliente = new Tenant("Ibertec");
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(plataforma);
        dbContext.ListaTenants.Add(cliente);

        var paymentProvider = new PaymentProviderFalso(
            Result.Exito(new SuscripcionProveedorDto("sub_123", "cus_999", EstadoSuscripcionProveedor.Desconocida)));

        var handler = new RegistrarSuscripcionTenantCommandHandler(
            dbContext, paymentProvider, new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new RegistrarSuscripcionTenantCommand(cliente.Id, "sub_123"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Comercial.EstadoNoReconocido");
        cliente.StripeSubscriptionId.Should().BeNull();
    }
}
