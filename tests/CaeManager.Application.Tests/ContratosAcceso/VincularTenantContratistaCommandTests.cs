using CaeManager.Application.ContratosAcceso.Commands.VincularTenantContratista;
using CaeManager.Application.Tests.AutorizacionesLectura;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.ContratosAcceso;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.ContratosAcceso;

/// <summary>
/// El vínculo del contrato de acceso con el Tenant de la Empresa contratista: misma autoridad que el resto
/// del contrato (la cartera del Cliente empresarial), nunca el propio Tenant, y una sola vez.
/// </summary>
public class VincularTenantContratistaCommandTests
{
    private readonly Guid _tenantPropio = Guid.NewGuid();
    private readonly Guid _clienteEmpresarial = Guid.NewGuid();
    private readonly ContratoAccesoRepositorioFalso _repositorio = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly ContratoAcceso _contrato;

    public VincularTenantContratistaCommandTests()
    {
        _contrato = ContratoAcceso.Crear(_clienteEmpresarial, Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 3, 1), null, null);
        _repositorio.Agregar(_contrato);
    }

    private VincularTenantContratistaCommandHandler Handler(AlcanceDatosServiceFalso? alcance = null) =>
        new(_repositorio, alcance ?? new AlcanceDatosServiceFalso(), new TenantActualFijo(_tenantPropio), _unitOfWork);

    [Fact]
    public async Task Vincula_el_Tenant_de_la_Empresa_contratista_y_guarda()
    {
        var tenantContratista = Guid.NewGuid();

        var resultado = await Handler().Handle(
            new VincularTenantContratistaCommand(_contrato.Id, tenantContratista, _contrato.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _contrato.TenantContratistaId.Should().Be(tenantContratista);
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Fuera_de_cartera_responde_como_si_el_contrato_no_existiera()
    {
        var fueraDeCartera = await Handler(new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: []))
            .Handle(new VincularTenantContratistaCommand(_contrato.Id, Guid.NewGuid()), CancellationToken.None);
        var inexistente = await Handler()
            .Handle(new VincularTenantContratistaCommand(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        fueraDeCartera.Error.Codigo.Should().Be("ContratoAcceso.NoEncontrado");
        fueraDeCartera.Error.Should().Be(inexistente.Error);
        _contrato.TenantContratistaId.Should().BeNull();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task No_vincula_el_propio_Tenant()
    {
        var resultado = await Handler().Handle(new VincularTenantContratistaCommand(_contrato.Id, _tenantPropio), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.TenantContratistaPropio");
        _contrato.TenantContratistaId.Should().BeNull();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_contrato_ya_vinculado_no_cambia_de_Tenant()
    {
        var primero = Guid.NewGuid();
        _contrato.VincularTenantContratista(primero);

        var resultado = await Handler().Handle(new VincularTenantContratistaCommand(_contrato.Id, Guid.NewGuid()), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContratoAcceso.TenantContratistaYaVinculado");
        _contrato.TenantContratistaId.Should().Be(primero);
        _unitOfWork.VecesGuardado.Should().Be(0);
    }
}
