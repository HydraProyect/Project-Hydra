using CaeManager.Application.Tenants.Commands.ReactivarTenant;
using CaeManager.Application.Tenants.Commands.SuspenderTenant;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Plataforma;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Tenants.Commands;

public class SuspenderTenantCommandTests
{
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly BloqueoFalso _bloqueo = new();

    private SuspenderTenantCommandHandler Suspender(Tenant tenant, AutorizacionAdminPlataformaFalsa autorizacion, Guid? usuario = null)
    {
        var contexto = new TenantsQueryContextFalso();
        contexto.ListaTenants.Add(tenant);
        return new(contexto, autorizacion, new CurrentUserServiceFalso(usuario ?? Guid.NewGuid()), new TransaccionDeComandoFalsa(), _bloqueo, _bloqueo, _unitOfWork);
    }

    private ReactivarTenantCommandHandler Reactivar(Tenant tenant, AutorizacionAdminPlataformaFalsa autorizacion)
    {
        var contexto = new TenantsQueryContextFalso();
        contexto.ListaTenants.Add(tenant);
        return new(contexto, autorizacion, new CurrentUserServiceFalso(Guid.NewGuid()), new TransaccionDeComandoFalsa(), _bloqueo, _bloqueo, _unitOfWork);
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
    public async Task Decide_con_el_estado_leido_bajo_el_candado_y_no_con_la_copia_del_circuito()
    {
        // El contexto del circuito conserva el Tenant suspendido; otra sesión ya lo reactivó.
        var copiaDelCircuito = new Tenant("Tenant A");
        copiaDelCircuito.Suspender();
        var actual = new Tenant("Tenant A");
        var contexto = new TenantsQueryContextFalso();
        contexto.ListaTenants.Add(copiaDelCircuito);
        _bloqueo.AlDescartar = () =>
        {
            contexto.ListaTenants.Clear();
            contexto.ListaTenants.Add(actual);
        };
        var handler = new SuspenderTenantCommandHandler(contexto, AutorizacionAdminPlataformaFalsa.Global(),
            new CurrentUserServiceFalso(Guid.NewGuid()), new TransaccionDeComandoFalsa(), _bloqueo, _bloqueo, _unitOfWork);

        var resultado = await handler.Handle(new(actual.Id), default);

        resultado.EsExitoso.Should().BeTrue();
        actual.Estado.Should().Be(EstadoTenant.Suspendido);
        _bloqueo.Pasos.Should().Equal("bloquear", "descartar");
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

    private sealed class BloqueoFalso : IBloqueoEstadoOperativoTenant, IDescarteCambiosPendientes
    {
        public List<string> Pasos { get; } = [];
        public Action? AlDescartar { get; set; }

        public Task BloquearAsync(Guid tenantId, CancellationToken cancellationToken = default)
        {
            Pasos.Add("bloquear");
            return Task.CompletedTask;
        }

        public void DescartarCambiosPendientes()
        {
            Pasos.Add("descartar");
            AlDescartar?.Invoke();
        }
    }
}
