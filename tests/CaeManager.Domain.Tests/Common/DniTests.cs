using CaeManager.Domain.Common;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Common;

public class DniTests
{
    [Theory]
    [InlineData("12345678Z")]
    [InlineData("X1234567L")] // NIE
    public void Crea_un_documento_espanol_valido(string valor)
    {
        var dni = Dni.Crear(valor);

        dni.Valor.Should().Be(valor);
    }

    [Fact]
    public void Rechaza_un_dni_con_digito_de_control_incorrecto()
    {
        var accion = () => Dni.Crear("12345678A");

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("AAA123456")] // TIE, sin dígito de control que comprobar
    [InlineData("123456789")] // pasaporte extranjero
    public void Acepta_documentos_extranjeros_sin_validacion_estricta(string valor)
    {
        var dni = Dni.Crear(valor);

        dni.Valor.Should().Be(valor);
    }

    [Fact]
    public void Normaliza_minusculas_y_espacios()
    {
        var dni = Dni.Crear("  12345678z  ");

        dni.Valor.Should().Be("12345678Z");
    }

    [Fact]
    public void Rechaza_vacio()
    {
        var accion = () => Dni.Crear("   ");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Dos_documentos_con_el_mismo_valor_son_iguales()
    {
        Dni.Crear("12345678Z").Should().Be(Dni.Crear("12345678z"));
    }

    [Fact]
    public void Se_convierte_a_string_implicitamente()
    {
        string valor = Dni.Crear("12345678Z");

        valor.Should().Be("12345678Z");
    }
}
