using CaeManager.Domain.Common;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Common;

public class CifTests
{
    [Theory]
    [InlineData("B12345674")]
    [InlineData("P1234567D")]
    public void Crea_un_cif_valido(string valor)
    {
        var cif = Cif.Crear(valor);

        cif.Valor.Should().Be(valor);
    }

    [Fact]
    public void Rechaza_un_cif_con_digito_de_control_incorrecto()
    {
        var accion = () => Cif.Crear("B12345671");

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("12345678Z")] // DNI válido, pero no es un CIF
    [InlineData("AAA123456")] // TIE, tampoco es un CIF
    public void Rechaza_documentos_que_no_son_cif_aunque_sean_validos_como_otro_tipo(string valor)
    {
        var accion = () => Cif.Crear(valor);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Normaliza_minusculas_y_espacios()
    {
        var cif = Cif.Crear("  b12345674  ");

        cif.Valor.Should().Be("B12345674");
    }

    [Fact]
    public void Rechaza_vacio()
    {
        var accion = () => Cif.Crear(string.Empty);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Se_convierte_a_string_implicitamente()
    {
        string valor = Cif.Crear("B12345674");

        valor.Should().Be("B12345674");
    }
}
