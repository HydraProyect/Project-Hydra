using CaeManager.Domain.Common;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Common;

public class ValidadorIdentificacionTests
{
    [Theory]
    [InlineData("12345678Z")]
    [InlineData("77189989B")]
    public void Detecta_un_dni_valido(string dni)
    {
        var resultado = ValidadorIdentificacion.Analizar(dni);

        resultado.Tipo.Should().Be(TipoIdentificacion.Dni);
        resultado.EsValido.Should().BeTrue();
    }

    [Fact]
    public void Detecta_un_dni_con_digito_de_control_incorrecto()
    {
        var resultado = ValidadorIdentificacion.Analizar("12345678A");

        resultado.Tipo.Should().Be(TipoIdentificacion.Dni);
        resultado.EsValido.Should().BeFalse();
    }

    [Theory]
    [InlineData("X1234567L")]
    [InlineData("Y2345678Z")]
    public void Detecta_un_nie_valido(string nie)
    {
        var resultado = ValidadorIdentificacion.Analizar(nie);

        resultado.Tipo.Should().Be(TipoIdentificacion.Nie);
        resultado.EsValido.Should().BeTrue();
    }

    [Fact]
    public void Detecta_un_nie_con_digito_de_control_incorrecto()
    {
        var resultado = ValidadorIdentificacion.Analizar("X1234567A");

        resultado.Tipo.Should().Be(TipoIdentificacion.Nie);
        resultado.EsValido.Should().BeFalse();
    }

    [Theory]
    [InlineData("B12345674")] // letra de organización con dígito de control numérico
    [InlineData("P1234567D")] // letra de organización con dígito de control alfabético
    public void Detecta_un_cif_de_empresa_valido(string cif)
    {
        var resultado = ValidadorIdentificacion.Analizar(cif);

        resultado.Tipo.Should().Be(TipoIdentificacion.NifEmpresa);
        resultado.EsValido.Should().BeTrue();
    }

    [Fact]
    public void Detecta_un_cif_con_digito_de_control_incorrecto()
    {
        var resultado = ValidadorIdentificacion.Analizar("B12345671");

        resultado.Tipo.Should().Be(TipoIdentificacion.NifEmpresa);
        resultado.EsValido.Should().BeFalse();
    }

    [Fact]
    public void Detecta_un_numero_de_soporte_tie_sin_validacion_de_digito_de_control()
    {
        var resultado = ValidadorIdentificacion.Analizar("AAA123456");

        resultado.Tipo.Should().Be(TipoIdentificacion.TieSoporte);
        resultado.EsValido.Should().BeTrue();
    }

    [Theory]
    [InlineData("123456789")] // pasaporte numérico extranjero
    [InlineData("AB1C2D3E4")]
    public void Trata_formatos_no_espanoles_como_otros_sin_bloquear(string documento)
    {
        var resultado = ValidadorIdentificacion.Analizar(documento);

        resultado.Tipo.Should().Be(TipoIdentificacion.Otros);
    }

    [Fact]
    public void Normaliza_minusculas_y_espacios_antes_de_analizar()
    {
        var resultado = ValidadorIdentificacion.Analizar("  12345678z  ");

        resultado.Tipo.Should().Be(TipoIdentificacion.Dni);
        resultado.EsValido.Should().BeTrue();
    }
}
