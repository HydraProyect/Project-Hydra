using CaeManager.Domain.Common;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Common;

public class EmailTests
{
    [Theory]
    [InlineData("persona@ejemplo.com")]
    [InlineData("nombre.apellido+etiqueta@sub.ejemplo.es")]
    public void Crea_un_email_valido(string valor)
    {
        var email = Email.Crear(valor);

        email.Valor.Should().Be(valor);
    }

    [Theory]
    [InlineData("no-es-un-email")]
    [InlineData("@ejemplo.com")]
    [InlineData("persona@")]
    public void Rechaza_formatos_invalidos(string valor)
    {
        var accion = () => Email.Crear(valor);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_vacio()
    {
        var accion = () => Email.Crear("   ");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Recorta_espacios_alrededor()
    {
        var email = Email.Crear("  persona@ejemplo.com  ");

        email.Valor.Should().Be("persona@ejemplo.com");
    }

    [Fact]
    public void Se_convierte_a_string_implicitamente()
    {
        string valor = Email.Crear("persona@ejemplo.com");

        valor.Should().Be("persona@ejemplo.com");
    }
}
