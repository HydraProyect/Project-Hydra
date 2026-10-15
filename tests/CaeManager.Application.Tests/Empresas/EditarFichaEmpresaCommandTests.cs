using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Commands.EditarFichaEmpresa;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Empresas;

public class EditarFichaEmpresaCommandTests
{
    private static EditarFichaEmpresaCommand Comando(Empresa empresa) => new(empresa.Id, empresa.Version,
        "DocuSign", true, "Ana", "Ajena", "Prevención", "Luis", "123", "spa@example.test",
        "Mutua", "Eva", "456", "formacion@example.test", "Rosa", "789", "prl@example.test");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Solo_guarda_con_alcance_de_gestion(bool gestion)
    {
        var empresa = new Empresa("Empresa de prueba");
        var repositorio = new EmpresaRepositorioFalso();
        repositorio.Empresas.Add(empresa);
        var uow = new UnitOfWorkFalso();
        var handler = new EditarFichaEmpresaCommandHandler(repositorio,
            new AlcanceDatosServiceFalso(empresaIdsParaGestion: gestion ? null : []), uow);
        var resultado = await handler.Handle(Comando(empresa), CancellationToken.None);
        resultado.EsExitoso.Should().Be(gestion);
        uow.VecesGuardado.Should().Be(gestion ? 1 : 0);
        empresa.FirmanteDocumentos.Should().Be(gestion ? "Ana" : null);
    }

    [Fact]
    public async Task Una_version_antigua_no_modifica_la_ficha()
    {
        var empresa = new Empresa("Empresa de prueba");
        var repositorio = new EmpresaRepositorioFalso();
        repositorio.Empresas.Add(empresa);
        var uow = new UnitOfWorkFalso();
        var handler = new EditarFichaEmpresaCommandHandler(repositorio, new AlcanceDatosServiceFalso(), uow);
        var resultado = await handler.Handle(Comando(empresa) with { Version = Guid.NewGuid() }, CancellationToken.None);
        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        empresa.FirmanteDocumentos.Should().BeNull();
        uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_empresa_inexistente_no_guarda()
    {
        var empresa = new Empresa("Empresa de prueba");
        var uow = new UnitOfWorkFalso();
        var handler = new EditarFichaEmpresaCommandHandler(new EmpresaRepositorioFalso(),
            new AlcanceDatosServiceFalso(), uow);
        var resultado = await handler.Handle(Comando(empresa), CancellationToken.None);
        resultado.Error.Codigo.Should().Be("Empresa.NoEncontrada");
        empresa.FirmanteDocumentos.Should().BeNull();
        uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Guardar_la_ficha_vacia_suprime_los_datos_anteriores()
    {
        var empresa = new Empresa("Empresa de prueba");
        var repositorio = new EmpresaRepositorioFalso();
        repositorio.Empresas.Add(empresa);
        var uow = new UnitOfWorkFalso();
        var handler = new EditarFichaEmpresaCommandHandler(repositorio, new AlcanceDatosServiceFalso(), uow);
        await handler.Handle(Comando(empresa), CancellationToken.None);
        var vacio = Comando(empresa) with
        {
            CanalFirmaDocumentos = null,
            SelloCustodiadoPorOperadorCae = null,
            FirmanteDocumentos = " ",
            ModalidadOrganizacionPreventiva = null,
            OrganizacionSpa = null,
            ContactoSpaNombre = null,
            ContactoSpaTelefono = null,
            ContactoSpaCorreo = null,
            Mutua = null,
            ContactoFormacionNombre = null,
            ContactoFormacionTelefono = null,
            ContactoFormacionCorreo = null,
            TecnicoPrlNombre = null,
            TecnicoPrlTelefono = null,
            TecnicoPrlCorreo = null
        };
        var resultado = await handler.Handle(vacio, CancellationToken.None);
        resultado.EsExitoso.Should().BeTrue();
        empresa.FirmanteDocumentos.Should().BeNull();
        empresa.ContactoSpaCorreo.Should().BeNull();
        empresa.ContactoFormacionNombre.Should().BeNull();
        empresa.TecnicoPrlCorreo.Should().BeNull();
        empresa.Mutua.Should().BeNull();
        empresa.SelloCustodiadoPorOperadorCae.Should().BeNull();
        uow.VecesGuardado.Should().Be(2);
    }

    [Fact]
    public void El_validador_rechaza_canales_correos_y_longitudes_invalidos()
    {
        var validator = new EditarFichaEmpresaCommandValidator();
        var comando = Comando(new Empresa("Empresa de prueba"));
        validator.Validate(comando).IsValid.Should().BeTrue();
        var resultado = validator.Validate(comando with
        {
            Id = Guid.Empty,
            Version = Guid.Empty,
            CanalFirmaDocumentos = "Otro",
            ContactoSpaCorreo = "invalido",
            TecnicoPrlTelefono = new string('x', 51)
        });
        resultado.Errors.Select(e => e.PropertyName).Should().BeEquivalentTo(
            ["Id", "Version", "CanalFirmaDocumentos", "ContactoSpaCorreo", "TecnicoPrlTelefono"]);
    }

    // Cada correo y cada longitud se miden por separado: una regla retirada de un solo campo pone su caso en rojo.
    [Theory]
    [InlineData(nameof(EditarFichaEmpresaCommand.ContactoSpaCorreo), "invalido")]
    [InlineData(nameof(EditarFichaEmpresaCommand.ContactoFormacionCorreo), "invalido")]
    [InlineData(nameof(EditarFichaEmpresaCommand.TecnicoPrlCorreo), "invalido")]
    [InlineData(nameof(EditarFichaEmpresaCommand.FirmanteDocumentos), 201)]
    [InlineData(nameof(EditarFichaEmpresaCommand.ModalidadOrganizacionPreventiva), 201)]
    [InlineData(nameof(EditarFichaEmpresaCommand.OrganizacionSpa), 201)]
    [InlineData(nameof(EditarFichaEmpresaCommand.ContactoSpaNombre), 201)]
    [InlineData(nameof(EditarFichaEmpresaCommand.ContactoSpaTelefono), 51)]
    [InlineData(nameof(EditarFichaEmpresaCommand.Mutua), 201)]
    [InlineData(nameof(EditarFichaEmpresaCommand.ContactoFormacionNombre), 201)]
    [InlineData(nameof(EditarFichaEmpresaCommand.ContactoFormacionTelefono), 51)]
    [InlineData(nameof(EditarFichaEmpresaCommand.TecnicoPrlNombre), 201)]
    [InlineData(nameof(EditarFichaEmpresaCommand.TecnicoPrlTelefono), 51)]
    public void El_validador_rechaza_cada_campo_invalido_por_separado(string campo, object valor)
    {
        var comando = Comando(new Empresa("Empresa de prueba"));
        var texto = valor is int longitud ? new string('x', longitud) : (string)valor;
        var invalido = campo switch
        {
            nameof(EditarFichaEmpresaCommand.ContactoSpaCorreo) => comando with { ContactoSpaCorreo = texto },
            nameof(EditarFichaEmpresaCommand.ContactoFormacionCorreo) => comando with { ContactoFormacionCorreo = texto },
            nameof(EditarFichaEmpresaCommand.TecnicoPrlCorreo) => comando with { TecnicoPrlCorreo = texto },
            nameof(EditarFichaEmpresaCommand.FirmanteDocumentos) => comando with { FirmanteDocumentos = texto },
            nameof(EditarFichaEmpresaCommand.ModalidadOrganizacionPreventiva) => comando with { ModalidadOrganizacionPreventiva = texto },
            nameof(EditarFichaEmpresaCommand.OrganizacionSpa) => comando with { OrganizacionSpa = texto },
            nameof(EditarFichaEmpresaCommand.ContactoSpaNombre) => comando with { ContactoSpaNombre = texto },
            nameof(EditarFichaEmpresaCommand.ContactoSpaTelefono) => comando with { ContactoSpaTelefono = texto },
            nameof(EditarFichaEmpresaCommand.Mutua) => comando with { Mutua = texto },
            nameof(EditarFichaEmpresaCommand.ContactoFormacionNombre) => comando with { ContactoFormacionNombre = texto },
            nameof(EditarFichaEmpresaCommand.ContactoFormacionTelefono) => comando with { ContactoFormacionTelefono = texto },
            nameof(EditarFichaEmpresaCommand.TecnicoPrlNombre) => comando with { TecnicoPrlNombre = texto },
            nameof(EditarFichaEmpresaCommand.TecnicoPrlTelefono) => comando with { TecnicoPrlTelefono = texto },
            _ => throw new ArgumentOutOfRangeException(nameof(campo), campo, null),
        };

        new EditarFichaEmpresaCommandValidator().Validate(invalido).Errors.Select(e => e.PropertyName)
            .Should().Equal(campo);
    }
}
