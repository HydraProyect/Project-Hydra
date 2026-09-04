using CaeManager.Application.Cumplimiento.Commands.RegistrarInstruccionTratamientoIaTenantPropietario;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Plataforma;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Cumplimiento;

public class RegistrarInstruccionTratamientoIaTenantPropietarioCommandHandlerTests
{
    private static RegistrarInstruccionTratamientoIaTenantPropietarioCommand ComandoValido(Guid tenantId) =>
        new(tenantId, "Draft-2026-09-03", "Draft-2026-09-03");

    [Fact]
    public async Task Sin_capacidad_AdminPlataforma_sobre_ese_tenant_falla()
    {
        var tenant = new Tenant("Refrielectric");
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(tenant);
        var repositorio = new InstruccionTratamientoIaTenantPropietarioRepositoryFalso();
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new RegistrarInstruccionTratamientoIaTenantPropietarioCommandHandler(
            dbContext, AutorizacionAdminPlataformaFalsa.SinNada(),
            new CurrentUserServiceFalso(Guid.NewGuid()), repositorio, unitOfWork);

        var resultado = await handler.Handle(ComandoValido(tenant.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("InstruccionTratamientoIa.SinAutoridad");
        repositorio.Filas.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Contra_el_tenant_de_plataforma_falla()
    {
        var plataforma = new Tenant("TALVEG");
        plataforma.MarcarComoPlataforma();
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(plataforma);
        var repositorio = new InstruccionTratamientoIaTenantPropietarioRepositoryFalso();

        var handler = new RegistrarInstruccionTratamientoIaTenantPropietarioCommandHandler(
            dbContext, AutorizacionAdminPlataformaFalsa.Global(),
            new CurrentUserServiceFalso(Guid.NewGuid()), repositorio, new UnitOfWorkFalso());

        var resultado = await handler.Handle(ComandoValido(plataforma.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Tenant.NoEncontrado");
        repositorio.Filas.Should().BeEmpty();
    }

    [Fact]
    public async Task Contra_un_tenant_ya_con_instruccion_vigente_falla_sin_apilar_una_segunda()
    {
        var tenant = new Tenant("Refrielectric");
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(tenant);
        var repositorio = new InstruccionTratamientoIaTenantPropietarioRepositoryFalso();
        repositorio.Agregar(new Domain.Cumplimiento.InstruccionTratamientoIaTenantPropietario(
            "v1", "v1", DateTime.UtcNow.AddDays(-10), Domain.Cumplimiento.OrigenInstruccionTratamientoIa.AltaManualPlataforma, Guid.NewGuid()));

        var handler = new RegistrarInstruccionTratamientoIaTenantPropietarioCommandHandler(
            dbContext, AutorizacionAdminPlataformaFalsa.Global(),
            new CurrentUserServiceFalso(Guid.NewGuid()), repositorio, new UnitOfWorkFalso());

        var resultado = await handler.Handle(ComandoValido(tenant.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("InstruccionTratamientoIa.YaVigente");
        repositorio.Filas.Should().HaveCount(1);
    }

    [Fact]
    public async Task Con_autoridad_sobre_un_tenant_valido_y_sin_instruccion_previa_registra_y_guarda()
    {
        var tenant = new Tenant("Refrielectric");
        var dbContext = new TenantsQueryContextFalso();
        dbContext.ListaTenants.Add(tenant);
        var repositorio = new InstruccionTratamientoIaTenantPropietarioRepositoryFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var autorizacion = AutorizacionAdminPlataformaFalsa.AcotadaA(tenant.Id);

        var handler = new RegistrarInstruccionTratamientoIaTenantPropietarioCommandHandler(
            dbContext, autorizacion, new CurrentUserServiceFalso(Guid.NewGuid()), repositorio, unitOfWork);

        var resultado = await handler.Handle(ComandoValido(tenant.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        repositorio.Filas.Should().ContainSingle();
        repositorio.Filas[0].VersionDpaAceptada.Should().Be("Draft-2026-09-03");
        repositorio.Filas[0].EstaVigente.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(1);
        autorizacion.UltimoTenantConsultado.Should().Be(tenant.Id);
    }
}
