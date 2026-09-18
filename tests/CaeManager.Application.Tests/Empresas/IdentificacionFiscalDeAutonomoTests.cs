using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Clientes.Commands.EditarCliente;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Commands.EditarEmpresa;
using CaeManager.Application.Subcontratas.Commands.CrearSubcontrata;
using CaeManager.Application.Subcontratas.Commands.EditarSubcontrata;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace CaeManager.Application.Tests.Empresas;

/// <summary>
/// P1 — un autónomo (persona física) tiene que poder darse de alta. Las seis
/// vías con validador exigían un NIF de persona jurídica y rechazaban un DNI
/// o un NIE aunque su dígito de control fuera correcto, así que el alta era
/// imposible sin inventarse un CIF.
///
/// Los seis validadores comparten desde este incremento un único criterio
/// (<c>ValidadorIdentificacion.EsIdentificacionFiscalValida</c>) en vez de
/// seis copias del mismo método privado. Esta clase los prueba uno a uno a
/// propósito: si alguno se desengancha del criterio común, la cobertura de
/// los otros cinco no lo delata.
/// </summary>
public class IdentificacionFiscalDeAutonomoTests
{
    private const string DniValido = "77189989B";
    private const string NieValido = "X1234567L";
    private const string CifValido = "B12345674";
    private const string DniConControlIncorrecto = "77189989A";
    private const string SoporteTie = "AAA123456";

    private static readonly Guid Id = Guid.NewGuid();

    public static TheoryData<string> IdentificacionesAdmitidas() => new() { CifValido, DniValido, NieValido };

    public static TheoryData<string> IdentificacionesRechazadas() =>
        new() { DniConControlIncorrecto, SoporteTie, "123456789", "B12345670" };

    // --- Alta y edición de Empresa -------------------------------------

    [Theory]
    [MemberData(nameof(IdentificacionesAdmitidas))]
    public void CrearEmpresa_admite_cif_dni_y_nie(string identificacion)
    {
        var resultado = new CrearEmpresaCommandValidator()
            .Validate(new CrearEmpresaCommand("Marta Ruiz Salas", identificacion, []));

        resultado.IsValid.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(IdentificacionesRechazadas))]
    public void CrearEmpresa_rechaza_lo_que_no_es_una_identificacion_fiscal(string identificacion)
    {
        var resultado = new CrearEmpresaCommandValidator()
            .Validate(new CrearEmpresaCommand("Marta Ruiz Salas", identificacion, []));

        DebeFallarPorIdentificacionFiscal(resultado);
    }

    [Fact]
    public void CrearEmpresa_sigue_exigiendo_una_identificacion_fiscal()
    {
        var resultado = new CrearEmpresaCommandValidator()
            .Validate(new CrearEmpresaCommand("Marta Ruiz Salas", null, []));

        resultado.IsValid.Should().BeFalse("el alta la exige desde MVP-1 — este incremento amplía qué vale, no la vuelve opcional");
    }

    [Theory]
    [MemberData(nameof(IdentificacionesAdmitidas))]
    public void EditarEmpresa_admite_cif_dni_y_nie(string identificacion)
    {
        var resultado = new EditarEmpresaCommandValidator()
            .Validate(new EditarEmpresaCommand(Id, "Marta Ruiz Salas", identificacion, []));

        resultado.IsValid.Should().BeTrue();
    }

    [Fact]
    public void EditarEmpresa_sigue_admitiendo_una_empresa_sin_identificacion_fiscal()
    {
        var resultado = new EditarEmpresaCommandValidator()
            .Validate(new EditarEmpresaCommand(Id, "Limpiezas del Norte S.L.", null, []));

        resultado.IsValid.Should().BeTrue("hay Empresas legacy sin ella y editar otro campo no debe forzar su relleno");
    }

    // --- Alta y edición de Cliente empresarial --------------------------

    [Theory]
    [MemberData(nameof(IdentificacionesAdmitidas))]
    public void CrearCliente_admite_cif_dni_y_nie(string identificacion)
    {
        var resultado = new CrearClienteCommandValidator()
            .Validate(new CrearClienteCommand("Marta Ruiz Salas", identificacion, false, null));

        resultado.IsValid.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(IdentificacionesRechazadas))]
    public void CrearCliente_rechaza_lo_que_no_es_una_identificacion_fiscal(string identificacion)
    {
        var resultado = new CrearClienteCommandValidator()
            .Validate(new CrearClienteCommand("Marta Ruiz Salas", identificacion, false, null));

        DebeFallarPorIdentificacionFiscal(resultado);
    }

    [Theory]
    [MemberData(nameof(IdentificacionesAdmitidas))]
    public void EditarCliente_admite_cif_dni_y_nie(string identificacion)
    {
        var resultado = new EditarClienteCommandValidator()
            .Validate(new EditarClienteCommand(Id, "Marta Ruiz Salas", identificacion, false, null));

        resultado.IsValid.Should().BeTrue();
    }

    // --- Alta y edición de Subcontrata ----------------------------------

    [Theory]
    [MemberData(nameof(IdentificacionesAdmitidas))]
    public void CrearSubcontrata_admite_cif_dni_y_nie(string identificacion)
    {
        var resultado = new CrearSubcontrataCommandValidator()
            .Validate(new CrearSubcontrataCommand("Marta Ruiz Salas", identificacion, [], []));

        resultado.IsValid.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(IdentificacionesRechazadas))]
    public void CrearSubcontrata_rechaza_lo_que_no_es_una_identificacion_fiscal(string identificacion)
    {
        var resultado = new CrearSubcontrataCommandValidator()
            .Validate(new CrearSubcontrataCommand("Marta Ruiz Salas", identificacion, [], []));

        DebeFallarPorIdentificacionFiscal(resultado);
    }

    [Theory]
    [MemberData(nameof(IdentificacionesAdmitidas))]
    public void EditarSubcontrata_admite_cif_dni_y_nie(string identificacion)
    {
        var resultado = new EditarSubcontrataCommandValidator()
            .Validate(new EditarSubcontrataCommand(Id, "Marta Ruiz Salas", identificacion, [], []));

        resultado.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// El mensaje nombra los tres documentos admitidos: un autónomo que teclea
    /// su DNI mal escrito tiene que poder enterarse de que el campo sí acepta
    /// un DNI. Un "El CIF no es válido." le decía lo contrario.
    /// </summary>
    private static void DebeFallarPorIdentificacionFiscal(ValidationResult resultado)
    {
        resultado.IsValid.Should().BeFalse();
        resultado.Errors.Should().Contain(e => e.ErrorMessage == Empresa.MensajeIdentificacionFiscalInvalida);
    }
}
