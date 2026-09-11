using CaeManager.Application.Comercial.Commands.ActualizarEstadoComercialTenant;
using CaeManager.Application.Comercial.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Tenants;
using CaeManager.Application.Tests.Plataforma;
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

    /// <summary>
    /// Hallazgo Codex del 2026-09-11: a diferencia de RegistrarSuscripcionTenantCommand,
    /// esta resincronización no excluía al tenant de plataforma — si por deriva de datos
    /// tuviera un StripeSubscriptionId, el botón "Actualizar desde Stripe" le cambiaría el
    /// estado comercial y con ello el gate de escrituras. Se comprueba ANTES que
    /// SinSuscripcionVinculada a propósito: en el árbol sano nunca coexisten (el tenant de
    /// plataforma nunca tiene StripeSubscriptionId), así que solo este orden demuestra que
    /// la exclusión es real y no una coincidencia con la otra guarda.
    /// </summary>
    [Fact]
    public async Task Bloquea_la_resincronizacion_del_tenant_de_plataforma_aunque_tenga_una_suscripcion_vinculada()
    {
        var plataforma = CrearTenantPlataforma();
        plataforma.VincularSuscripcionStripe("cus_deriva", "sub_deriva", EstadoComercialTenant.Activa);
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(plataforma);

        var paymentProvider = new PaymentProviderFalso(
            CaeManager.Domain.Common.Result.Exito(new SuscripcionProveedorDto("sub_deriva", "cus_deriva", EstadoSuscripcionProveedor.Cancelada)));
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new ActualizarEstadoComercialTenantCommandHandler(
            dbContext, paymentProvider, AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id), unitOfWork);

        var resultado = await handler.Handle(new ActualizarEstadoComercialTenantCommand(plataforma.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Comercial.TenantPlataforma");
        plataforma.EstadoComercial.Should().Be(EstadoComercialTenant.Activa);
        paymentProvider.UltimoIdConsultado.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(0);
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
            dbContext, new PaymentProviderFalso(), AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id), new UnitOfWorkFalso());

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
            dbContext, paymentProvider, AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", plataforma.Id), unitOfWork);

        var resultado = await handler.Handle(new ActualizarEstadoComercialTenantCommand(cliente.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        cliente.EstadoComercial.Should().Be(EstadoComercialTenant.Suspendida);
        paymentProvider.UltimoIdConsultado.Should().Be("sub_123");
        unitOfWork.VecesGuardado.Should().Be(1);
    }
}
