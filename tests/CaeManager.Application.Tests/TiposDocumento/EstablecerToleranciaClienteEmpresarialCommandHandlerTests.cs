using CaeManager.Application.Centros.Commands.EstablecerDocumentacionRequeridaCentro;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.TiposDocumento.Commands.EstablecerToleranciaClienteEmpresarial;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.TiposDocumento;

/// <summary>
/// La tolerancia por defecto del Cliente empresarial (corrección del propietario, 2026-10-03): se guarda dispersa (sin fila =
/// 0, no se guardan ceros), con el mismo control de alcance que la lectura por IA por Cliente empresarial, y con la cota
/// técnica de <see cref="TipoDocumentoCentro.ToleranciaMaximaDias"/>.
/// </summary>
public class EstablecerToleranciaClienteEmpresarialCommandHandlerTests
{
    private readonly ToleranciaDocumentoClienteEmpresarialRepositorioFalso _repositorio = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly Empresa _cliente = Empresa.CrearComoCliente("Cliente empresarial", "B12345674", false, null, null);
    private readonly CaeManager.Domain.Documentos.TipoDocumento _tipo = new("Certificado", null, false, 1, AmbitoAplicacion.Empresa);

    public EstablecerToleranciaClienteEmpresarialCommandHandlerTests()
    {
        _empresas.ListaEmpresas.Add(_cliente);
        _tipos.ListaTiposDocumento.Add(_tipo);
    }

    private EstablecerToleranciaClienteEmpresarialCommandHandler Handler(AlcanceDatosServiceFalso? alcance = null) =>
        new(_repositorio, _empresas, _tipos, alcance ?? new AlcanceDatosServiceFalso(), _unitOfWork);

    [Fact]
    public async Task Fija_una_tolerancia_nueva_y_la_cambia_despues()
    {
        var resultado = await Handler().Handle(new EstablecerToleranciaClienteEmpresarialCommand(_cliente.Id, _tipo.Id, 15), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _repositorio.Tolerancias.Should().ContainSingle().Which.ToleranciaDias.Should().Be(15);

        await Handler().Handle(new EstablecerToleranciaClienteEmpresarialCommand(_cliente.Id, _tipo.Id, 30), CancellationToken.None);

        _repositorio.Tolerancias.Should().ContainSingle().Which.ToleranciaDias.Should().Be(30);
        _unitOfWork.VecesGuardado.Should().Be(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public async Task Cero_o_vacio_borra_la_fila_porque_sin_fila_la_tolerancia_es_cero(int? dias)
    {
        _repositorio.Tolerancias.Add(new ToleranciaDocumentoClienteEmpresarial(_cliente.Id, _tipo.Id, 15));

        var resultado = await Handler().Handle(new EstablecerToleranciaClienteEmpresarialCommand(_cliente.Id, _tipo.Id, dias), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _repositorio.Tolerancias.Should().BeEmpty();
    }

    [Fact]
    public async Task Cero_sin_fila_previa_no_guarda_ningun_cero()
    {
        await Handler().Handle(new EstablecerToleranciaClienteEmpresarialCommand(_cliente.Id, _tipo.Id, 0), CancellationToken.None);

        _repositorio.Tolerancias.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Cliente_empresarial_fuera_de_la_cartera_visible_se_rechaza_sin_escribir()
    {
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()]);

        var resultado = await Handler(alcance).Handle(new EstablecerToleranciaClienteEmpresarialCommand(_cliente.Id, _tipo.Id, 15), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ToleranciaCliente.SinAcceso");
        _repositorio.Tolerancias.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_Cliente_empresarial_de_la_cartera_visible_se_acepta()
    {
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [_cliente.Id]);

        var resultado = await Handler(alcance).Handle(new EstablecerToleranciaClienteEmpresarialCommand(_cliente.Id, _tipo.Id, 15), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _repositorio.Tolerancias.Should().ContainSingle();
    }

    [Fact]
    public async Task Un_Cliente_empresarial_o_un_tipo_que_no_existen_en_el_Tenant_se_rechazan()
    {
        var sinCliente = await Handler().Handle(new EstablecerToleranciaClienteEmpresarialCommand(Guid.NewGuid(), _tipo.Id, 15), CancellationToken.None);
        var sinTipo = await Handler().Handle(new EstablecerToleranciaClienteEmpresarialCommand(_cliente.Id, Guid.NewGuid(), 15), CancellationToken.None);

        sinCliente.Error.Codigo.Should().Be("ToleranciaCliente.ClienteNoEncontrado");
        sinTipo.Error.Codigo.Should().Be("ToleranciaCliente.TipoDocumentoNoEncontrado");
        _repositorio.Tolerancias.Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(TipoDocumentoCentro.ToleranciaMaximaDias, true)]
    [InlineData(TipoDocumentoCentro.ToleranciaMaximaDias + 1, false)]
    public void El_validador_del_Cliente_empresarial_acota_la_tolerancia(int dias, bool valido)
    {
        new EstablecerToleranciaClienteEmpresarialCommandValidator()
            .Validate(new EstablecerToleranciaClienteEmpresarialCommand(Guid.NewGuid(), Guid.NewGuid(), dias))
            .IsValid.Should().Be(valido);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(TipoDocumentoCentro.ToleranciaMaximaDias, true)]
    [InlineData(TipoDocumentoCentro.ToleranciaMaximaDias + 1, false)]
    public void El_validador_del_Centro_acota_la_tolerancia_y_deja_vacia_heredar(int? dias, bool valido)
    {
        new EstablecerDocumentacionRequeridaCentroCommandValidator()
            .Validate(new EstablecerDocumentacionRequeridaCentroCommand(Guid.NewGuid(), Guid.NewGuid(), true, null, true, null, null, dias))
            .IsValid.Should().Be(valido);
    }
}
