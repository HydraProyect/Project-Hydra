using CaeManager.Application.Tenants.Commands.ReactivarTenant;
using CaeManager.Application.Tenants.Commands.SuspenderTenant;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Plataforma;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Tenants.Commands;

public class SuspenderTenantCommandTests
{
    private readonly UnitOfWorkFalso _unitOfWork = new();

    private SuspenderTenantCommandHandler Suspender(Tenant tenant, AutorizacionAdminPlataformaFalsa autorizacion, Guid? usuario = null)
    {
        var contexto = new TenantsQueryContextFalso();
        contexto.ListaTenants.Add(tenant);
        return new(contexto, autorizacion, new CurrentUserServiceFalso(usuario ?? Guid.NewGuid()), _unitOfWork);
    }

    private ReactivarTenantCommandHandler Reactivar(Tenant tenant, AutorizacionAdminPlataformaFalsa autorizacion)
    {
        var contexto = new TenantsQueryContextFalso();
        contexto.ListaTenants.Add(tenant);
        return new(contexto, autorizacion, new CurrentUserServiceFalso(Guid.NewGuid()), _unitOfWork);
    }

    [Fact]
    public async Task Un_Actor_de_Plataforma_autorizado_suspende_y_sella_las_claves()
    {
        var tenant = new Tenant("Tenant A");

        var resultado = await Suspender(tenant, AutorizacionAdminPlataformaFalsa.AcotadaA(tenant.Id))
            .Handle(new(tenant.Id), default);

        resultado.EsExitoso.Should().BeTrue();
        tenant.Estado.Should().Be(EstadoTenant.Suspendido);
        tenant.ClavesApiValidasDesdeUtc.Should().NotBeNull();
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Sin_autorizacion_sobre_ese_Tenant_no_se_suspende()
    {
        var tenant = new Tenant("Tenant A");

        var resultado = await Suspender(tenant, AutorizacionAdminPlataformaFalsa.AcotadaA(Guid.NewGuid()))
            .Handle(new(tenant.Id), default);

        resultado.EsExitoso.Should().BeFalse();
        tenant.Estado.Should().Be(EstadoTenant.Activo);
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task El_Tenant_de_plataforma_no_se_suspende()
    {
        var tenant = new Tenant("Plataforma", esPlataforma: true);

        var resultado = await Suspender(tenant, AutorizacionAdminPlataformaFalsa.Global()).Handle(new(tenant.Id), default);

        resultado.EsExitoso.Should().BeFalse();
        tenant.Estado.Should().Be(EstadoTenant.Activo);
    }

    [Fact]
    public async Task Suspender_dos_veces_no_mueve_el_sello_de_las_claves()
    {
        var tenant = new Tenant("Tenant A");
        tenant.Suspender();
        var sello = tenant.ClavesApiValidasDesdeUtc;

        var resultado = await Suspender(tenant, AutorizacionAdminPlataformaFalsa.Global()).Handle(new(tenant.Id), default);

        resultado.EsExitoso.Should().BeTrue();
        tenant.ClavesApiValidasDesdeUtc.Should().Be(sello);
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Reactivar_devuelve_el_acceso_pero_no_las_claves_anteriores()
    {
        var tenant = new Tenant("Tenant A");
        tenant.Suspender();
        var emitidaDuranteLaSuspension = tenant.ClavesApiValidasDesdeUtc!.Value.AddTicks(1);

        var resultado = await Reactivar(tenant, AutorizacionAdminPlataformaFalsa.AcotadaA(tenant.Id))
            .Handle(new(tenant.Id), default);

        resultado.EsExitoso.Should().BeTrue();
        tenant.Estado.Should().Be(EstadoTenant.Activo);
        Tenant.AdmiteClaveEmitidaEn(tenant.Estado, tenant.EstadoComercial, tenant.ClavesApiValidasDesdeUtc, emitidaDuranteLaSuspension)
            .Should().BeFalse();
    }

    [Fact]
    public async Task Reactivar_un_Tenant_activo_no_mueve_el_sello_de_las_claves()
    {
        var tenant = new Tenant("Tenant A");

        var resultado = await Reactivar(tenant, AutorizacionAdminPlataformaFalsa.Global()).Handle(new(tenant.Id), default);

        resultado.EsExitoso.Should().BeTrue();
        tenant.ClavesApiValidasDesdeUtc.Should().BeNull();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_autorizacion_sobre_ese_Tenant_no_se_reactiva()
    {
        var tenant = new Tenant("Tenant A");
        tenant.Suspender();

        var resultado = await Reactivar(tenant, AutorizacionAdminPlataformaFalsa.AcotadaA(Guid.NewGuid()))
            .Handle(new(tenant.Id), default);

        resultado.EsExitoso.Should().BeFalse();
        tenant.Estado.Should().Be(EstadoTenant.Suspendido);
    }
}
