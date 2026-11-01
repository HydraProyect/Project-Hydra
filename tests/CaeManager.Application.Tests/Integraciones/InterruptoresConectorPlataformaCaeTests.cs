using CaeManager.Application.Comercial.Commands.CambiarAddOnSubidaAutomatica;
using CaeManager.Application.Integraciones.Commands.CambiarConectorApiProveedorPlataforma;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Plataforma;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class InterruptoresConectorPlataformaCaeTests
{
    private readonly UnitOfWorkFalso _unitOfWork = new();

    [Fact]
    public async Task El_kill_switch_de_la_API_lo_cambia_quien_tiene_autorizacion_global()
    {
        var proveedores = new ProveedorPlataformaCaeRepositorioFalso();
        var proveedor = new ProveedorPlataformaCae("plataforma-x", "Plataforma X");
        proveedores.Agregar(proveedor);
        var handler = new CambiarConectorApiProveedorPlataformaCommandHandler(
            proveedores, AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid()), _unitOfWork);

        (await handler.Handle(new(proveedor.Id, true), default)).EsExitoso.Should().BeTrue();
        proveedor.ConectorApiActivo.Should().BeTrue();

        (await handler.Handle(new(proveedor.Id, false), default)).EsExitoso.Should().BeTrue();
        proveedor.ConectorApiActivo.Should().BeFalse();
    }

    [Fact]
    public async Task Una_autorizacion_acotada_a_un_Tenant_no_cambia_el_kill_switch_global()
    {
        var proveedores = new ProveedorPlataformaCaeRepositorioFalso();
        var proveedor = new ProveedorPlataformaCae("plataforma-x", "Plataforma X");
        proveedores.Agregar(proveedor);
        var handler = new CambiarConectorApiProveedorPlataformaCommandHandler(
            proveedores, AutorizacionAdminPlataformaFalsa.AcotadaA(Guid.NewGuid()), new CurrentUserServiceFalso(Guid.NewGuid()), _unitOfWork);

        (await handler.Handle(new(proveedor.Id, true), default)).EsExitoso.Should().BeFalse();

        proveedor.ConectorApiActivo.Should().BeFalse();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task El_Add_on_lo_activa_un_Actor_de_Plataforma_autorizado_sobre_ese_Tenant()
    {
        var tenant = new Tenant("Tenant A");
        var handler = HandlerAddOn(tenant, AutorizacionAdminPlataformaFalsa.AcotadaA(tenant.Id));

        (await handler.Handle(new(tenant.Id, true), default)).EsExitoso.Should().BeTrue();

        tenant.AddOnSubidaAutomaticaActivo.Should().BeTrue();
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Sin_autorizacion_sobre_ese_Tenant_el_Add_on_no_cambia()
    {
        var tenant = new Tenant("Tenant A");
        var handler = HandlerAddOn(tenant, AutorizacionAdminPlataformaFalsa.AcotadaA(Guid.NewGuid()));

        (await handler.Handle(new(tenant.Id, true), default)).EsExitoso.Should().BeFalse();

        tenant.AddOnSubidaAutomaticaActivo.Should().BeFalse();
    }

    [Fact]
    public async Task El_Tenant_de_plataforma_no_contrata_el_Add_on()
    {
        var tenant = new Tenant("Plataforma", esPlataforma: true);
        var handler = HandlerAddOn(tenant, AutorizacionAdminPlataformaFalsa.Global());

        (await handler.Handle(new(tenant.Id, true), default)).EsExitoso.Should().BeFalse();

        tenant.AddOnSubidaAutomaticaActivo.Should().BeFalse();
    }

    private CambiarAddOnSubidaAutomaticaCommandHandler HandlerAddOn(Tenant tenant, AutorizacionAdminPlataformaFalsa autorizacion)
    {
        var contexto = new TenantsQueryContextFalso();
        contexto.ListaTenants.Add(tenant);
        return new(contexto, autorizacion, new CurrentUserServiceFalso(Guid.NewGuid()), _unitOfWork);
    }
}
