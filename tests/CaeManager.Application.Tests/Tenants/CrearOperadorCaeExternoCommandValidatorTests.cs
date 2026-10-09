using CaeManager.Application.Tenants.Commands.CrearOperadorCaeExterno;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// FS-22: el alta de un Operador CAE externo lleva el correo y el nombre de su primer
/// Administrador, los dos obligatorios. Aquí solo la forma de la petición; que sin ellos
/// no se crea nada lo prueba el handler contra la base (<c>CrearOperadorCaeExternoTests</c>).
/// </summary>
public class CrearOperadorCaeExternoCommandValidatorTests
{
    private readonly CrearOperadorCaeExternoCommandValidator _validador = new();

    private static CrearOperadorCaeExternoCommand Alta(
        string nombreOperador = "Operador Sur", string email = "marta@operador-sur.test", string nombre = "Marta Ruiz") =>
        new(nombreOperador, email, nombre);

    [Fact]
    public void Con_nombre_correo_y_nombre_del_primer_Administrador_es_valido()
    {
        _validador.Validate(Alta()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Sin_correo_del_primer_Administrador_no_es_valido(string email)
    {
        var resultado = _validador.Validate(Alta(email: email));

        resultado.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be("El correo del primer Administrador es obligatorio.");
    }

    [Theory]
    [InlineData("marta")]
    [InlineData("marta@")]
    [InlineData("@operador-sur.test")]
    public void Un_correo_sin_forma_de_correo_no_es_valido(string email)
    {
        var resultado = _validador.Validate(Alta(email: email));

        resultado.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be("El correo del primer Administrador no tiene forma de correo.");
    }

    [Fact]
    public void Un_correo_mas_largo_que_la_columna_de_Identity_no_es_valido()
    {
        var dominio = "@operador-sur.test";
        var justo = new string('a', CrearOperadorCaeExternoCommandValidator.LongitudMaximaEmail - dominio.Length) + dominio;

        _validador.Validate(Alta(email: justo)).IsValid.Should().BeTrue("control: en el límite todavía cabe");
        _validador.Validate(Alta(email: "a" + justo)).Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be("El correo del primer Administrador es demasiado largo.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Sin_nombre_del_primer_Administrador_no_es_valido(string nombre)
    {
        var resultado = _validador.Validate(Alta(nombre: nombre));

        resultado.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be("El nombre del primer Administrador es obligatorio.");
    }

    [Fact]
    public void Un_nombre_del_primer_Administrador_demasiado_largo_no_es_valido()
    {
        var justo = new string('a', CrearOperadorCaeExternoCommandValidator.LongitudMaximaNombreAdministrador);

        _validador.Validate(Alta(nombre: justo)).IsValid.Should().BeTrue("control: en el límite todavía cabe");
        _validador.Validate(Alta(nombre: justo + "a")).Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be("El nombre del primer Administrador es demasiado largo.");
    }

    [Fact]
    public void El_nombre_del_Operador_CAE_externo_sigue_siendo_obligatorio()
    {
        _validador.Validate(Alta(nombreOperador: "")).Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be("El nombre del Operador CAE externo es obligatorio.");
    }
}
